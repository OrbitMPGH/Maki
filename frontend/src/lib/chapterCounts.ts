/**
 * Chapters a series has had: on disk now, plus those whose file Maki deleted on purpose. Read
 * progress is measured against this rather than `chapterFileCount`, since clearing read files must
 * not turn a finished series back into one with chapters left.
 */
export function obtainedCount(s: { chapterFileCount: number; removedChapterCount?: number }): number {
  return s.chapterFileCount + (s.removedChapterCount ?? 0)
}
