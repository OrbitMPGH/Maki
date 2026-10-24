import { t as staticT } from '@lingui/core/macro'
import type { ChapterProgressDto } from '../../api/reader'
import type { ChapterDto } from '../../api/types'

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

export type FileOrigin = { label: string; scraped: boolean; hint: string }
