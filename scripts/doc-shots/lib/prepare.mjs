import { DatabaseSync } from 'node:sqlite'
import { join } from 'node:path'

const DAY = 86400000
const ALIGNED_KEY = 'docshots.alignedAt'

// Values this far before the aligned time are history (old release dates) and stay put.
const SHIFT_BEFORE = 400 * DAY
// Values this far after it are either long-lived (token expiry) or were written by a later,
// unaligned run, so they stay put too.
const SHIFT_AFTER = 21 * DAY

const SKIP_TABLES = new Set(['AppConfig', 'UserSettings', 'sqlite_stat1', '__EFMigrationsHistory', '__EFMigrationsLock'])
// Facts about the world rather than about this library's activity.
const SKIP_COLUMNS = new Set(['AnimeSignals.StartDate', 'AnimeSignals.EndDate'])

const STAMP = /^(\d{4})-(\d{2})-(\d{2})(?:([ T])(\d{2}):(\d{2})(?::(\d{2})(?:\.(\d+))?)?)?(Z|[+-]\d{2}:\d{2})?$/

function parse(text) {
  const m = STAMP.exec(text)
  if (!m) return null
  const [, y, mo, d, sep, h, mi, s, frac, tz] = m
  const fracMs = frac ? Number((frac + '000').slice(0, 3)) : 0
  const ms = Date.UTC(+y, +mo - 1, +d, +(h ?? 0), +(mi ?? 0), +(s ?? 0), fracMs)
  return { ms, sep, hasSeconds: s !== undefined, frac, tz: tz ?? '' }
}

function format(p, ms) {
  const d = new Date(ms)
  const pad = (n, w = 2) => String(n).padStart(w, '0')
  const date = `${d.getUTCFullYear()}-${pad(d.getUTCMonth() + 1)}-${pad(d.getUTCDate())}`
  if (!p.sep) return date
  let time = `${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())}`
  if (p.hasSeconds) time += `:${pad(d.getUTCSeconds())}`
  if (p.frac) time += '.' + (pad(d.getUTCMilliseconds(), 3) + p.frac.slice(3)).slice(0, p.frac.length).padEnd(p.frac.length, '0')
  return `${date}${p.sep}${time}${p.tz}`
}

function shiftDates(db, delta, aligned) {
  const lo = aligned - SHIFT_BEFORE
  const hi = aligned + SHIFT_AFTER
  const tables = db.prepare("select name from sqlite_master where type = 'table'").all().map((r) => r.name)
  let changed = 0
  for (const table of tables) {
    if (SKIP_TABLES.has(table)) continue
    for (const col of db.prepare(`pragma table_info("${table}")`).all()) {
      if (!/TEXT/i.test(col.type) || SKIP_COLUMNS.has(`${table}.${col.name}`)) continue
      const rows = db
        .prepare(`select rowid id, "${col.name}" v from "${table}" where "${col.name}" glob '[12][0-9][0-9][0-9]-[01][0-9]-[0-3][0-9]*'`)
        .all()
      if (rows.length === 0) continue
      const update = db.prepare(`update "${table}" set "${col.name}" = ? where rowid = ?`)
      for (const { id, v } of rows) {
        const p = parse(v)
        if (!p || p.ms < lo || p.ms > hi) continue
        const step = p.sep ? delta : Math.round(delta / DAY) * DAY
        update.run(format(p, p.ms + step), id)
        changed++
      }
    }
  }
  return changed
}

function getConfig(db, key) {
  return db.prepare('select Value from AppConfig where Key = ?').get(key)?.Value
}

function setConfig(db, key, value) {
  db.prepare('insert into AppConfig (Key, Value) values (?, ?) on conflict(Key) do update set Value = excluded.Value').run(key, value)
}

/**
 * Chapter states the docs show. Each step is idempotent: it sets the same rows to the same
 * values every run, so the shots stay the same between runs.
 */
function stageStates(db, states) {
  const log = []
  const seriesId = (title) => {
    const row = db.prepare('select Id from Series where Title like ? order by Id limit 1').get(`%${title}%`)
    if (!row) throw new Error(`No series titled like "${title}" in the showcase library.`)
    return row.Id
  }
  const chapterIds = (title, numbers) => {
    const id = seriesId(title)
    return numbers.map((n) => db.prepare('select Id, ChapterFileId from Chapters where SeriesId = ? and Number = ?').get(id, n)).filter(Boolean)
  }

  // The showcase's files are database rows only. A health scan flags every one of them as
  // missing, so keep only the ones a shot shows as missing on purpose.
  const keepMissing = new Set()
  for (const { series, numbers } of states.missing ?? []) {
    for (const c of chapterIds(series, numbers)) if (c.ChapterFileId) keepMissing.add(c.ChapterFileId)
  }
  const stale = db
    .prepare('select Id, ChapterFileId from HealthFiles where Size < 0')
    .all()
    .filter((r) => !keepMissing.has(r.ChapterFileId))
  const dropVersion = db.prepare('delete from HealthFileVersions where FileId = ?')
  const dropFile = db.prepare('delete from HealthFiles where Id = ?')
  const dropFindings = db.prepare('delete from HealthFindings where FileId = ?')
  for (const r of stale) {
    dropFindings.run(r.Id)
    dropVersion.run(r.Id)
    dropFile.run(r.Id)
  }
  log.push(`cleared ${stale.length} missing-file records`)

  for (const { series, numbers } of states.missing ?? []) {
    for (const c of chapterIds(series, numbers)) {
      if (!c.ChapterFileId) continue
      const f = db.prepare('select RelativePath, SeriesId from ChapterFiles where Id = ?').get(c.ChapterFileId)
      const root = db.prepare('select RootFolderId from Series where Id = ?').get(f.SeriesId).RootFolderId
      if (db.prepare('select 1 from HealthFiles where ChapterFileId = ?').get(c.ChapterFileId)) continue
      db.prepare(
        `insert into HealthFiles (RootFolderId, SeriesId, ChapterFileId, RelativePath, Version, Size, ModifiedAt, AnalyzerVersion, Status, AnalysisJson, AnalyzedAt, Removed, VerifiedVersion)
         values (?, ?, ?, ?, ?, -1, '0001-01-01 00:00:00', 5, 'complete', ?, ?, 0, 2)`,
      ).run(
        root,
        f.SeriesId,
        c.ChapterFileId,
        f.RelativePath.replaceAll('/', '\\'),
        crypto.randomUUID().replaceAll('-', ''),
        JSON.stringify({ status: 'complete', hash: null, pages: [], problems: [{ kind: 'missing', severity: 'error', messageKey: 'health.finding.missing', paramsJson: null }], verified: true }),
        new Date().toISOString().replace('T', ' ').replace('Z', ''),
      )
    }
  }

  // The Health page lists a missing file only through an open finding.
  const stamp = new Date().toISOString().replace('T', ' ').replace('Z', '')
  for (const hf of db.prepare('select Id, Version from HealthFiles where Size < 0').all()) {
    if (db.prepare('select 1 from HealthFindings where FileId = ? and Version = ?').get(hf.Id, hf.Version)) continue
    db.prepare(
      `insert into HealthFindings (FileId, Version, Kind, Severity, Message, State, CreatedAt, MessageKey, ParamsJson)
       values (?, ?, 'missing', 'error', '', 'open', ?, 'health.finding.missing', null)`,
    ).run(hf.Id, hf.Version, stamp)
  }

  // Read and then cleaned up: the file is gone and the chapter remembers when.
  const removedAt = new Date(Date.now() - 2 * DAY).toISOString().replace('T', ' ').replace('Z', '0000')
  for (const { series, numbers } of states.removed ?? []) {
    for (const c of chapterIds(series, numbers)) {
      if (c.ChapterFileId) {
        db.prepare('delete from HealthFiles where ChapterFileId = ?').run(c.ChapterFileId)
        db.prepare('update Chapters set ChapterFileId = null where Id = ?').run(c.Id)
        db.prepare('delete from ChapterFiles where Id = ? and not exists (select 1 from Chapters where ChapterFileId = ?)').run(c.ChapterFileId, c.ChapterFileId)
      }
      db.prepare('update Chapters set FileRemovedAt = coalesce(FileRemovedAt, ?), FileAutoRemoved = 1 where Id = ?').run(removedAt, c.Id)
    }
  }

  // Gone from every source that listed it.
  for (const { series, numbers } of states.delisted ?? []) {
    for (const c of chapterIds(series, numbers)) {
      db.prepare('update Chapters set DelistedAt = coalesce(DelistedAt, ?) where Id = ?').run(removedAt, c.Id)
    }
  }

  // Wanted but not on disk yet, so the series offers "Download N wanted".
  for (const { series, numbers } of states.wanted ?? []) {
    for (const c of chapterIds(series, numbers)) {
      if (!c.ChapterFileId) db.prepare('update Chapters set Wanted = 1 where Id = ?').run(c.Id)
    }
  }

  // Out of the library, for the Archived list.
  const archivedAt = new Date(Date.now() - 5 * DAY).toISOString().replace('T', ' ').replace('Z', '0000')
  for (const title of states.archived ?? []) {
    db.prepare('update Series set ArchivedAt = coalesce(ArchivedAt, ?), ArchivedByUserId = coalesce(ArchivedByUserId, 1) where Id = ?').run(archivedAt, seriesId(title))
  }
  return log
}

/**
 * The showcase's tracker tokens are fake, so a real sync only produces errors. Stamping the last
 * sync as now keeps the hourly tick from firing during a run, and errors earlier runs left behind
 * are turned back into plain synced rows.
 */
function quietTrackers(db, now) {
  setConfig(db, 'scrobble.lastsyncat', new Date(now).toISOString())
  // The showcase reads a shared catalogue it never refreshes itself, which Health would flag as stale.
  setConfig(db, 'mangabaka.dumprefreshedat', new Date(now - DAY).toISOString())
  const fixed = db
    .prepare(
      `update SeriesScrobbleStates set Error = null, Status = 'reading',
         Chapter = (select count(*) from ChapterProgress p where p.SeriesId = SeriesScrobbleStates.SeriesId and p.UserId = SeriesScrobbleStates.UserId and p.Completed = 1)
       where Error is not null`,
    )
    .run().changes
  const dropped = db.prepare("delete from ScrobbleLog where Level = 'error'").run().changes
  // The seeded inbox has no message keys; anything keyed was added by a later run (level-ups,
  // health alerts about the services the showcase leaves unconfigured), so the bell count stays put.
  const inbox = db.prepare('delete from UserNotifications where MessageKey is not null').run().changes
  return `quietened trackers (${fixed} states, ${dropped} log errors) and the inbox (${inbox} added by runs)`
}

export function prepare(configDir, states, now = Date.now()) {
  const db = new DatabaseSync(join(configDir, 'maki.db'))
  try {
    db.exec('pragma busy_timeout = 5000')
    db.exec('begin immediate')
    let aligned = Date.parse(getConfig(db, ALIGNED_KEY) ?? '')
    if (Number.isNaN(aligned)) {
      // First run: the data was generated around the last reading session.
      const last = db.prepare('select max(EndedAt) m from ReadingSessions').get().m
      aligned = parse(last).ms + 3 * 3600000
    }
    const delta = now - aligned
    const shifted = delta > 60000 ? shiftDates(db, delta, aligned) : 0
    setConfig(db, ALIGNED_KEY, new Date(now).toISOString())
    const log = [...stageStates(db, states), quietTrackers(db, now)]
    db.exec('commit')
    db.exec('pragma wal_checkpoint(truncate)')
    return { days: delta / DAY, shifted, log }
  } catch (err) {
    if (db.isTransaction) db.exec('rollback')
    throw err
  } finally {
    db.close()
  }
}
