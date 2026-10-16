// Gives the showcase library the look of an account used for a couple of months.
// Run with the backend stopped. "data" writes the history, "achievements" dates the unlocks the
// backend computed from it (start the backend and open the stats page in between).
import { existsSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { DatabaseSync } from 'node:sqlite'

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..')
const configDir = process.env.MAKI_CONFIG_DIR ?? join(root, '.screenshots')
const mode = process.argv[2] ?? 'data'
const DAYS = 74
const USER_ID = 1

const dbPath = join(configDir, 'maki.db')
if (!existsSync(dbPath)) throw new Error(`No database at ${dbPath}`)
const db = new DatabaseSync(dbPath)
db.exec('PRAGMA busy_timeout = 5000')

function mulberry32(seed) {
  return () => {
    seed |= 0
    seed = (seed + 0x6d2b79f5) | 0
    let t = Math.imul(seed ^ (seed >>> 15), 1 | seed)
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296
  }
}
const rand = mulberry32(20261016)
const between = (a, b) => a + rand() * (b - a)
const intBetween = (a, b) => Math.floor(between(a, b + 1))
const pick = (arr) => arr[Math.floor(rand() * arr.length)]
const iso = (d) => d.toISOString().replace('T', ' ').replace('Z', '0000')
const MIN = 60_000
const HOUR = 3_600_000
const DAY = 86_400_000

const now = new Date()
const windowStart = new Date(now.getTime() - DAYS * DAY)

const columnCache = new Map()
function insert(table, row) {
  if (!columnCache.has(table)) {
    columnCache.set(table, db.prepare(`PRAGMA table_info(${table})`).all())
  }
  const full = { ...row }
  for (const col of columnCache.get(table)) {
    if (col.name in full || col.pk) continue
    if (col.notnull && col.dflt_value === null) full[col.name] = /INT|REAL|BOOL/i.test(col.type) ? 0 : ''
  }
  const names = Object.keys(full)
  const sql = `INSERT INTO ${table} (${names.map((n) => `"${n}"`).join(',')}) VALUES (${names.map(() => '?').join(',')})`
  return db.prepare(sql).run(...names.map((n) => full[n])).lastInsertRowid
}

if (mode === 'achievements') {
  const rows = db.prepare('SELECT Id, Key, Tier FROM UserAchievements WHERE UserId = ?').all(USER_ID)
  const update = db.prepare('UPDATE UserAchievements SET UnlockedAt = ?, SeenAt = ? WHERE Id = ?')
  for (const r of rows) {
    const progress = Math.min(0.97, 0.12 + r.Tier * 0.15 + rand() * 0.12)
    const at = iso(new Date(windowStart.getTime() + progress * DAYS * DAY))
    update.run(at, at, r.Id)
  }
  db.prepare('DELETE FROM UserNotifications WHERE UserId = ?').run(USER_ID)
  console.log(`Dated ${rows.length} achievement unlocks and cleared the inbox.`)
  db.close()
  process.exit(0)
}

const FINISHED = ['Uzumaki', 'Silent Voice', 'DEATH NOTE']
const READING = [
  'Dandadan', 'Frieren', 'SPY', 'Witch Hat', 'Blue Lock', 'Chainsaw', 'Jujutsu', 'Solo Leveling',
  'Sakamoto', 'Fragrant', 'Kaguya', 'Dress-Up', 'Oshi no Ko', 'Haikyu',
]
const STARTED = ['Ouran', 'Noragami', 'ONE PIECE', 'ATTACK ON TITAN', 'Demon Slayer', 'VINLAND', 'TOKYO GHOUL', 'Dr. STONE', 'Komi', 'Tokyo Revengers']

const series = db.prepare('SELECT Id, Title, FolderName, Type, TotalChapters FROM Series ORDER BY Id').all()
const findByTitle = (needle) => series.find((s) => s.Title.toLowerCase().includes(needle.toLowerCase()))
const assigned = new Set()
const take = (needles) =>
  needles.map(findByTitle).filter((s) => s && !assigned.has(s.Id) && assigned.add(s.Id)).map((s) => s)
const finished = take(FINISHED)
const reading = take(READING)
const started = take(STARTED)
const unread = series.filter((s) => !assigned.has(s.Id))

db.exec('BEGIN')

for (const t of ['ChapterProgress', 'ReadingStates', 'ReadingSessions', 'StatsEvents']) {
  db.prepare(`DELETE FROM ${t}`).run()
}
db.prepare('UPDATE Chapters SET ChapterFileId = NULL, Wanted = 0').run()
db.prepare('DELETE FROM ChapterFiles').run()
db.prepare("UPDATE AspNetUsers SET MaxContentRating = 'suggestive' WHERE Id = ?").run(USER_ID)

// Chapters, with a synthetic list for any series whose sources did not answer.
const chaptersBySeries = new Map()
for (const s of series) {
  let rows = db
    .prepare('SELECT Id, Number, Volume FROM Chapters WHERE SeriesId = ? AND Number >= 1 ORDER BY Number')
    .all(s.Id)
    .filter((c, i, all) => Number.isInteger(c.Number) && (i === 0 || all[i - 1].Number !== c.Number))
  if (rows.length < 5) {
    const n = Math.min(s.TotalChapters || 60, 220)
    for (let i = 1; i <= n; i++) {
      const release = new Date(now.getTime() - (n - i) * 6 * DAY - rand() * DAY)
      const id = insert('Chapters', {
        SeriesId: s.Id, Number: i, NumberRaw: String(i), Title: `Chapter ${i}`, Language: 'en',
        Wanted: 0, IsOneShot: 0, ReleaseDate: iso(release),
      })
      rows.push({ Id: Number(id), Number: i, Volume: null })
    }
  }
  chaptersBySeries.set(s.Id, rows)
}

// What each series looks like for this reader.
const plan = new Map()
const readTarget = (s, kind) => {
  const n = chaptersBySeries.get(s.Id).length
  if (kind === 'finished') return n
  if (kind === 'reading') return Math.max(10, Math.min(40, Math.round(n * between(0.08, 0.25))))
  if (kind === 'started') return Math.min(n, intBetween(2, 9))
  return 0
}
const addPlan = (list, kind, addedFrom, addedTo) => {
  for (const s of list) {
    const n = chaptersBySeries.get(s.Id).length
    const read = Math.min(n, readTarget(s, kind))
    let disk
    if (kind === 'finished') disk = n
    else if (kind === 'reading') disk = Math.min(n, read + intBetween(8, 45))
    else if (kind === 'started') disk = Math.min(n, read + intBetween(5, 30))
    else disk = Math.min(n, intBetween(3, Math.max(6, Math.round(n * 0.3))), 60)
    plan.set(s.Id, { s, kind, read, disk, addedDay: between(addedFrom, addedTo) })
  }
}
addPlan(finished, 'finished', 70, 62)
addPlan(reading, 'reading', 64, 30)
addPlan(started, 'started', 45, 14)
addPlan(unread, 'unread', 40, 1)

// Reading timeline: who reads how much on which day.
const dayWeights = []
for (let d = 0; d <= DAYS; d++) {
  const date = new Date(windowStart.getTime() + d * DAY)
  const weekend = date.getUTCDay() === 0 || date.getUTCDay() === 6
  const recent = DAYS - d < 26
  const active = recent || rand() < 0.86
  dayWeights.push(active ? (weekend ? 1.6 : 1) * between(0.35, 1.5) : 0)
}
const targets = [...plan.values()].filter((p) => p.read > 0)
const totalRead = targets.reduce((a, p) => a + p.read, 0)
const weightSum = dayWeights.reduce((a, b) => a + b, 0)
const remaining = new Map(targets.map((p) => [p.s.Id, p.read]))
const startDay = new Map(targets.map((p) => [p.s.Id, Math.max(0, Math.round(DAYS - p.addedDay + between(0, 2)))]))
const dayReads = Array.from({ length: DAYS + 1 }, () => [])

for (let d = 0; d <= DAYS; d++) {
  let budget = Math.round((totalRead * dayWeights[d]) / weightSum)
  if (d === DAYS) budget = Math.min(budget, 11)
  while (budget > 0) {
    const open = targets.filter((p) => remaining.get(p.s.Id) > 0 && startDay.get(p.s.Id) <= d)
    if (open.length === 0) break
    const urgent = open.filter((p) => p.kind === 'finished')
    const choice = rand() < 0.45 && urgent.length > 0 ? pick(urgent) : pick(open)
    const left = remaining.get(choice.s.Id)
    const n = Math.min(left, budget, intBetween(2, 8))
    dayReads[d].push({ plan: choice, from: 0, count: n })
    remaining.set(choice.s.Id, left - n)
    budget -= n
  }
}
for (const p of targets) {
  let left = remaining.get(p.s.Id)
  let d = DAYS - 2
  while (left > 0) {
    const n = Math.min(left, 6)
    dayReads[Math.max(0, d)].push({ plan: p, from: 0, count: n })
    left -= n
    d--
  }
}
// Chapters are read in order, so positions follow the calendar, whatever order they were planned in.
const nextChapter = new Map(targets.map((p) => [p.s.Id, 0]))
for (const items of dayReads) {
  for (const item of items) {
    item.from = nextChapter.get(item.plan.s.Id)
    nextChapter.set(item.plan.s.Id, item.from + item.count)
  }
}

const readAt = new Map()
const sessions = []
const eventRows = []
const progressRows = []
const lastRead = new Map()
const finishedAt = new Map()

for (let d = 0; d <= DAYS; d++) {
  const items = dayReads[d]
  if (items.length === 0) continue
  const dayStart = new Date(windowStart.getTime() + d * DAY)
  dayStart.setUTCHours(0, 0, 0, 0)
  const chaptersToday = items.reduce((a, i) => a + i.count, 0)
  const sittingCount = chaptersToday > 14 ? 3 : chaptersToday > 6 ? 2 : 1
  const perSitting = Math.ceil(items.length / sittingCount)
  const isToday = d === DAYS
  let cursorTime = dayStart.getTime() + (isToday ? 7 : 10) * HOUR + between(0, 90) * MIN
  for (let k = 0; k < sittingCount; k++) {
    const group = items.slice(k * perSitting, (k + 1) * perSitting)
    if (group.length === 0) continue
    const sittingStart = cursorTime
    let t = sittingStart
    let sittingSeconds = 0
    let sittingChapters = 0
    for (const item of group) {
      const chapters = chaptersBySeries.get(item.plan.s.Id)
      const manhwa = item.plan.s.Type === 'manhwa'
      let seriesSeconds = 0
      let lastTime = t
      for (let i = 0; i < item.count; i++) {
        const ch = chapters[item.from + i]
        const seconds = Math.round(manhwa ? between(300, 560) : between(190, 470))
        const pages = manhwa ? intBetween(36, 70) : intBetween(18, 32)
        const start = t
        t += seconds * 1000
        readAt.set(ch.Id, new Date(t))
        progressRows.push({
          UserId: USER_ID, SeriesId: item.plan.s.Id, ChapterId: ch.Id, PageIndex: pages - 1, PageCount: pages,
          Completed: 1, External: 0, ReadSeconds: seconds, ReportedSeconds: seconds, Watched: 0, BulkMarked: 0,
          StartedAt: iso(new Date(start)), UpdatedAt: iso(new Date(t)), CompletedAt: iso(new Date(t)),
        })
        seriesSeconds += seconds
        lastTime = t
        t += between(4, 40) * 1000
        sittingSeconds += seconds
        sittingChapters++
        lastRead.set(item.plan.s.Id, { at: lastTime, chapter: ch })
      }
      const stamp = iso(new Date(lastTime))
      eventRows.push({ Type: 3, Timestamp: stamp, UserId: USER_ID, SeriesId: item.plan.s.Id, SeriesTitle: item.plan.s.Title, Value: item.count })
      eventRows.push({ Type: 6, Timestamp: stamp, UserId: USER_ID, SeriesId: item.plan.s.Id, SeriesTitle: item.plan.s.Title, Value: seriesSeconds })
      if (item.from + item.count >= chapters.length && item.plan.kind === 'finished') {
        eventRows.push({ Type: 5, Timestamp: stamp, UserId: USER_ID, SeriesId: item.plan.s.Id, SeriesTitle: item.plan.s.Title, Value: 1 })
        finishedAt.set(item.plan.s.Id, lastTime)
      }
    }
    sessions.push({ UserId: USER_ID, StartedAt: iso(new Date(sittingStart)), EndedAt: iso(new Date(t)), ActiveSeconds: sittingSeconds, ChaptersCompleted: sittingChapters })
    cursorTime = t + between(100, 260) * MIN
  }
}

// Files on disk, dated no later than the read that needed them.
let onDisk = 0
let missing = 0
const downloadBuckets = new Map()
for (const p of plan.values()) {
  const chapters = chaptersBySeries.get(p.s.Id)
  const mapping = db.prepare('SELECT SourceName FROM SourceMappings WHERE SeriesId = ? ORDER BY Priority LIMIT 1').get(p.s.Id)
  const sourceName = mapping?.SourceName ?? 'mangadex'
  const added = new Date(now.getTime() - p.addedDay * DAY)
  db.prepare('UPDATE Series SET Added = ? WHERE Id = ?').run(iso(added), p.s.Id)
  db.prepare('UPDATE UserSeriesStates SET AddedToLibraryAtUtc = ? WHERE SeriesId = ? AND UserId = ?').run(iso(added), p.s.Id, USER_ID)
  eventRows.push({ Type: 0, Timestamp: iso(added), UserId: null, SeriesId: p.s.Id, SeriesTitle: p.s.Title, Value: 1 })

  chapters.forEach((ch, i) => {
    const wantsFile = i < p.disk
    if (!wantsFile) {
      const shouldWant = p.kind !== 'unread' && p.kind !== 'finished' && i < p.disk + (p.s.Id % 7 === 0 ? 12 : p.s.Id % 5 === 0 ? 5 : 0)
      if (shouldWant) {
        db.prepare('UPDATE Chapters SET Wanted = 1 WHERE Id = ?').run(ch.Id)
        missing++
      }
      return
    }
    const read = readAt.get(ch.Id)
    const latest = read ? read.getTime() - between(20, 2600) * MIN : now.getTime() - between(1, 30) * DAY
    const when = new Date(Math.max(added.getTime() + between(1, 90) * MIN, Math.min(latest, now.getTime() - HOUR)))
    const pages = intBetween(18, 34)
    const fileId = insert('ChapterFiles', {
      SeriesId: p.s.Id, DateAdded: iso(when), SourceName: sourceName,
      RelativePath: `${p.s.FolderName}/${p.s.Title} ${ch.Volume ? `Vol.${ch.Volume} ` : ''}Ch.${ch.Number}.cbz`,
      Size: Math.round(between(7, 26) * 1024 * 1024), PageCount: pages, ImageFormat: 'jpeg', Trusted: 0, Tier: 0,
    })
    db.prepare('UPDATE Chapters SET ChapterFileId = ?, Wanted = 1 WHERE Id = ?').run(fileId, ch.Id)
    onDisk++
    const key = `${p.s.Id}|${iso(when).slice(0, 10)}`
    const bucket = downloadBuckets.get(key) ?? { p, at: when, count: 0 }
    bucket.count++
    if (when > bucket.at) bucket.at = when
    downloadBuckets.set(key, bucket)
  })
}
for (const b of downloadBuckets.values()) {
  eventRows.push({ Type: 2, Timestamp: iso(b.at), UserId: null, SeriesId: b.p.s.Id, SeriesTitle: b.p.s.Title, Value: b.count })
}

for (const r of progressRows) insert('ChapterProgress', r)
for (const r of sessions) insert('ReadingSessions', r)
for (const r of eventRows) insert('StatsEvents', r)

// High-water marks, ratings and a few half-read chapters for the Continue reading rail.
const ratings = { finished: [9, 10, 8, 9, 7], reading: [8, 9, 7, 10, 8, 9] }
let ratingIndex = { finished: 0, reading: 0 }
const inProgress = []
for (const p of targets) {
  const chapters = chaptersBySeries.get(p.s.Id)
  const last = lastRead.get(p.s.Id)
  const lastNumber = chapters[p.read - 1].Number
  insert('ReadingStates', {
    UserId: USER_ID, SeriesId: p.s.Id, Title: p.s.Title, MaxChapter: lastNumber, MaxVolume: 0,
    Finished: p.kind === 'finished' ? 1 : 0, LastProgressAt: iso(new Date(last.at)), UpdatedAt: iso(new Date(last.at)),
  })
  if (ratings[p.kind] && ratingIndex[p.kind] < ratings[p.kind].length) {
    db.prepare('UPDATE UserSeriesStates SET Rating = ? WHERE SeriesId = ? AND UserId = ?').run(ratings[p.kind][ratingIndex[p.kind]++], p.s.Id, USER_ID)
  }
  if (p.kind === 'reading' && p.read < chapters.length && inProgress.length < 7) inProgress.push({ p, next: chapters[p.read], last })
}
inProgress.sort((a, b) => b.last.at - a.last.at).forEach((x, i) => {
  const pages = intBetween(22, 32)
  const at = new Date(now.getTime() - (i * 5 + 2) * HOUR)
  insert('ChapterProgress', {
    UserId: USER_ID, SeriesId: x.p.s.Id, ChapterId: x.next.Id, PageIndex: intBetween(4, pages - 6), PageCount: pages,
    Completed: 0, External: 0, ReadSeconds: intBetween(40, 180), ReportedSeconds: 0, Watched: 0, BulkMarked: 0,
    StartedAt: iso(at), UpdatedAt: iso(at),
  })
})

// Tracker connections that look signed in. The tokens are placeholders and are never sent anywhere
// unless a sync runs, which the last-sync time below keeps from happening for the next hour.
const setConfig = db.prepare('INSERT OR REPLACE INTO AppConfig (Key, Value) VALUES (?, ?)')
for (const key of [
  'scrobble.anilistclientid', 'scrobble.anilistclientsecret', 'scrobble.malclientid',
  'scrobble.malclientsecret', 'scrobble.kitsuclientid', 'scrobble.kitsuclientsecret',
]) setConfig.run(key, 'showcase')
setConfig.run('scrobble.intervalminutes', '60')
setConfig.run('scrobble.lastsyncat', now.toISOString())
const setUser = db.prepare('INSERT OR REPLACE INTO UserSettings (UserId, Key, Value) VALUES (?, ?, ?)')
setUser.run(USER_ID, 'scrobble.kitsuemail', 'maki@example.com')
setUser.run(USER_ID, 'scrobble.kitsupassword', 'showcase')
setUser.run(USER_ID, 'scrobble.mangabakatoken', 'showcase')
for (const t of ['ScrobbleTokens', 'SeriesScrobbleStates', 'ScrobbleLog']) db.prepare(`DELETE FROM ${t}`).run()
const trackers = [
  ['anilist', 'maki_reads', 1], ['mal', 'maki_reads', 1], ['kitsu', 'makireads', 0.6], ['mangabaka', 'maki', 0.4],
]
for (const [service, username] of trackers) {
  insert('ScrobbleTokens', { UserId: USER_ID, Service: service, AccessToken: 'showcase', ExpiresAt: iso(new Date(now.getTime() + 300 * DAY)), RefreshToken: 'showcase', Username: username })
}
const logRows = []
for (const p of targets) {
  const last = lastRead.get(p.s.Id)
  const chapter = Math.floor(chaptersBySeries.get(p.s.Id)[p.read - 1].Number)
  for (const [service, , share] of trackers) {
    if (rand() > share) continue
    const at = new Date(last.at + between(1, 9) * MIN)
    const status = p.kind === 'finished' ? 'completed' : 'reading'
    insert('SeriesScrobbleStates', { UserId: USER_ID, SeriesId: p.s.Id, Service: service, Chapter: chapter, Volume: 0, Status: status, SyncedAt: iso(at) })
    logRows.push({ at, service, title: p.s.Title, chapter, status })
  }
}
logRows.sort((a, b) => a.at - b.at).slice(-24).forEach((l) => {
  insert('ScrobbleLog', { UserId: USER_ID, Level: 'info', Service: l.service, Title: l.title, Message: `Synced chapter ${l.chapter} as ${l.status}`, Timestamp: iso(l.at) })
})

db.exec('COMMIT')
db.exec('PRAGMA wal_checkpoint(TRUNCATE)')

const hours = Math.round(progressRows.reduce((a, r) => a + r.ReadSeconds, 0) / 3600)
console.log({
  series: series.length, finished: finished.length, reading: reading.length, started: started.length,
  unread: unread.length, chaptersRead: progressRows.length, hoursRead: hours, filesOnDisk: onDisk, missing,
})
db.close()
