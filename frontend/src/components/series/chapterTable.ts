import { t as staticT } from '@lingui/core/macro'
import type { ChapterProgressDto } from '../../api/reader'
import type { SourceInfo } from '../../api/hooks'
import type { ChapterDto, SourceMappingDto } from '../../api/types'

export function chapterLabel(c: ChapterDto): string {
  // A volume entry: a volume file on disk standing in for chapters no source lists.
  if (!c.isOneShot && c.number === null && c.volume !== null) return staticT`Vol.${c.volume}`
  if (c.isOneShot || c.number === null) return c.title ?? staticT`One-shot`
  // Prefer the volume the backing file actually is; fall back to metadata volume.
  const volNum = c.fileVolume ?? (c.volume !== null ? String(c.volume) : null)
  const chapterNumber = c.number
  return volNum !== null ? staticT`Vol.${volNum} Ch.${chapterNumber}` : staticT`Ch.${chapterNumber}`
}

export interface ReadState {
  read: boolean
  /** A resume position exists and the chapter isn't finished. */
  inProgress: boolean
  /** Read according to Kavita rather than read here, so no page position is known. */
  external: boolean
  /** Ticked off without being read. Counts as read everywhere, but never reached the stats log. */
  watched: boolean
}

/**
 * Read state of one chapter row, straight off its `ChapterProgress` row, the only source of truth
 * for read state. Nothing is inferred from the series' high-water mark: that mark is forward-only
 * and covers every chapter numbered below it, so one stale Kavita read made a whole run of never
 * opened chapters look read, with no way to correct it.
 *
 * `completed` is the sticky read flag; `pageIndex` is a resume position that may move backwards, so
 * a row with a position but no completion is in progress. A row carrying `unreadAt` is a tombstone
 * left by an explicit mark-unread and is plainly unread: its zero position is not progress.
 */
export function readStateOf(p: ChapterProgressDto | undefined): ReadState {
  if (!p || p.unreadAt !== null) {
    return { read: false, inProgress: false, external: false, watched: false }
  }

  return {
    read: p.completed,
    inProgress: !p.completed && p.pageIndex > 0,
    external: p.external,
    watched: p.watched,
  }
}

/**
 * The chapter languages a series' enabled mappings ask their sources for, lower-cased. Null when
 * nothing narrows it down (no mapping, or a source that files chapters under whatever it lists),
 * which `isSkipped` reads as every language.
 */
export function readLanguagesOf(
  mappings: readonly SourceMappingDto[] | undefined,
  sources: readonly SourceInfo[] | undefined,
): ReadonlySet<string> | null {
  const languages = new Set<string>()
  for (const m of mappings ?? []) {
    if (!m.enabled) continue
    const codes = (m.languageFilter ?? '').split(',').map((code) => code.trim().toLowerCase()).filter(Boolean)
    if (codes.length > 0) {
      codes.forEach((code) => languages.add(code))
    } else if (sources?.find((source) => source.name === m.sourceName)?.supportsLanguageFilter) {
      languages.add('en')
    } else {
      return null
    }
  }
  return languages.size > 0 ? languages : null
}

/**
 * What `isSkipped` needs from the finished chapters: the highest finished number per language
 * (lower-cased code), and every finished number in any language.
 */
export type FinishedHighWater = {
  byLanguage: ReadonlyMap<string, number>
  numbers: ReadonlySet<number>
}

export function finishedHighWater(
  chapters: readonly ChapterDto[],
  progress: ReadonlyMap<number, ChapterProgressDto>,
): FinishedHighWater {
  const byLanguage = new Map<string, number>()
  const numbers = new Set<number>()
  for (const c of chapters) {
    if (c.isOneShot || c.number === null || !readStateOf(progress.get(c.id)).read) continue
    const language = c.language.toLowerCase()
    numbers.add(c.number)
    if (c.number > (byLanguage.get(language) ?? -Infinity)) byLanguage.set(language, c.number)
  }
  return { byLanguage, numbers }
}

/**
 * A chapter the reader walked past: numbered, wanted, unfinished, in a language they read, while a
 * higher-numbered chapter in that same language is finished. Volume entries and one-shots have no
 * number to be passed, and a chapter whose number was finished in another language was read, not
 * skipped. `readLanguages` of null means any language reads.
 */
export function isSkipped(
  chapter: ChapterDto,
  highWater: FinishedHighWater,
  progress: ReadonlyMap<number, ChapterProgressDto>,
  readLanguages: ReadonlySet<string> | null,
): boolean {
  if (chapter.isOneShot || chapter.number === null || !chapter.wanted) return false
  const language = chapter.language.toLowerCase()
  if (readLanguages !== null && !readLanguages.has(language)) return false
  if (readStateOf(progress.get(chapter.id)).read) return false
  if (highWater.numbers.has(chapter.number)) return false
  return chapter.number < (highWater.byLanguage.get(language) ?? -Infinity)
}

export function countSkipped(
  chapters: readonly ChapterDto[],
  progress: ReadonlyMap<number, ChapterProgressDto>,
  readLanguages: ReadonlySet<string> | null,
): number {
  const highWater = finishedHighWater(chapters, progress)
  return chapters.filter((c) => isSkipped(c, highWater, progress, readLanguages)).length
}

export type FileOrigin = { label: string; scraped: boolean; hint: string }
