import type { ReaderEnding, ReaderManifest } from '../../api/reader'
import type { ReleaseOutlookDto } from '../../api/releases'
import type { QueueItemDto } from '../../api/types'

const DAY_MS = 86_400_000

/**
 * Whether the series counts as finished at this end screen. The server marks it with the completing
 * write, which may not have landed yet, so a completed series whose last chapter was just read counts
 * from the manifest's own prediction. Nothing counts while a further chapter is on disk.
 */
export function seriesFinishedHere(
  manifest: Pick<ReaderManifest, 'seriesFinished' | 'finishesSeries' | 'nextChapterId'>,
  ending: Pick<ReaderEnding, 'finished' | 'nextChapterId'> | undefined,
  readingCounted: boolean,
): boolean {
  const next = ending ? ending.nextChapterId : manifest.nextChapterId
  if (next != null) return false
  return Boolean(ending?.finished || manifest.seriesFinished || (manifest.finishesSeries && readingCounted))
}

type Pace = NonNullable<ReaderManifest['readingPace']>

/** Seconds left in the series after `readCount` chapters, at the reader's own pace. Null with no pace or nothing left. */
export function seriesSecondsLeft(pace: Pace | null | undefined, readCount: number): number | null {
  if (!pace) return null
  const remaining = pace.totalChapters - readCount
  return remaining > 0 ? Math.round(pace.chapterSeconds * remaining) : null
}

/**
 * Time left while a chapter is open: the rest of this chapter by the share of pages still ahead,
 * and the series on top of it. Null until the reader has enough timed chapters to have a pace.
 */
export function timeLeft(
  manifest: Pick<ReaderManifest, 'readingPace' | 'seriesReadCount' | 'completed' | 'pageCount'>,
  page: number,
): { chapter: number; series: number | null } | null {
  const { readingPace: pace } = manifest
  if (!pace || manifest.pageCount <= 0) return null
  const share = Math.min(1, Math.max(0, (manifest.pageCount - page) / manifest.pageCount))
  const chapter = Math.round(pace.chapterSeconds * share)
  // An unfinished chapter is not in the read count, so its rest is added to the whole chapters after it.
  const after = pace.totalChapters - manifest.seriesReadCount - (manifest.completed ? 0 : 1)
  const wholeAfter = Math.max(0, after)
  const series = Math.round(pace.chapterSeconds * wholeAfter) + (manifest.completed ? 0 : chapter)
  return { chapter, series: series > chapter ? series : null }
}

export type ReleaseSpan =
  | { kind: 'range'; from: number; to: number; unit: 'days' | 'weeks' }
  | { kind: 'within'; to: number; unit: 'days' | 'weeks' }
  | { kind: 'soon' }
  | { kind: 'late' }

/**
 * The outlook's window as a span from now, never a date. Only an Expected or Late series has one;
 * quiet, irregular and unknown ones say nothing here.
 */
export function releaseSpan(outlook: ReleaseOutlookDto, now: number): ReleaseSpan | null {
  if (outlook.state === 'Late') return { kind: 'late' }
  if (outlook.state !== 'Expected' || !outlook.windowStart || !outlook.windowEnd) return null
  const start = new Date(outlook.windowStart).getTime()
  const end = new Date(outlook.windowEnd).getTime()
  if (Number.isNaN(start) || Number.isNaN(end)) return null
  const hi = Math.round((end - now) / DAY_MS)
  if (hi < 1) return { kind: 'soon' }
  const lo = Math.max(0, Math.round((start - now) / DAY_MS))
  if (hi >= 14) {
    const toWeeks = Math.round(hi / 7)
    const fromWeeks = Math.max(1, Math.min(toWeeks, Math.round(lo / 7)))
    return lo < 7 ? { kind: 'within', to: toWeeks, unit: 'weeks' } : { kind: 'range', from: fromWeeks, to: toWeeks, unit: 'weeks' }
  }
  return lo < 1 ? { kind: 'within', to: hi, unit: 'days' } : { kind: 'range', from: lo, to: Math.max(lo, hi), unit: 'days' }
}

const WORKING = new Set(['FetchingPages', 'Downloading', 'Validating', 'Packaging', 'Importing'])
const WAITING = new Set(['Queued', 'RateLimited', 'Resolving'])

/** The series' per-chapter queue rows that are still on their way, split by whether a worker has them. */
export function activeDownloads(items: QueueItemDto[]): { working: QueueItemDto[]; waiting: QueueItemDto[] } {
  const chapters = items.filter((q) => q.chapterId != null)
  return {
    working: chapters.filter((q) => WORKING.has(q.status)),
    waiting: chapters.filter((q) => WAITING.has(q.status)),
  }
}
