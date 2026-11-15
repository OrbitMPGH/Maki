import type { ReaderCredits } from '../../api/reader'

/**
 * The pages the reader shows, as chapter indexes from `first` to `last`. Credit pages left out are
 * always a run at the start and one at the end, so a window is all it takes: navigation, the strip,
 * the scrubber and the page counter work inside it, while image URLs, progress, bookmarks and notes
 * keep the chapter's own indexes.
 */
export interface PageWindow {
  first: number
  last: number
}

/** Chapters shorter than this hide nothing, so the reader does not ask. Matches `CreditDetector.MinPages`. */
export const CREDITS_MIN_PAGES = 6

/** The window `credits` leave, applying only the edges this visit takes. The whole chapter for anything that does not fit. */
export function creditWindow(pageCount: number, credits: ReaderCredits | undefined, head: boolean, tail: boolean): PageWindow {
  const whole = { first: 0, last: Math.max(0, pageCount - 1) }
  if (!credits) return whole
  const first = head ? credits.headPages : 0
  const last = pageCount - 1 - (tail ? credits.tailPages : 0)
  return first >= 0 && first <= last ? { first, last } : whole
}

export function clampToWindow(index: number, window: PageWindow): number {
  return Math.min(window.last, Math.max(window.first, index))
}

/**
 * The share of the last page shown that is left out from its bottom: a banner fused into the end of a
 * webtoon slice. Only the Continuous strip can end part way down a page, so a caller passes `tail`
 * only there and only for a visit that trims the end; 0 when nothing is cut.
 */
export function tailCut(pageCount: number, credits: ReaderCredits | undefined, window: PageWindow, tail: boolean): number {
  if (!tail || !credits || !(credits.tailOffset > 0) || credits.tailOffset >= 1) return 0
  return window.last === pageCount - 1 - credits.tailPages && window.first < window.last ? credits.tailOffset : 0
}

/** A position on the last page shown (a share of its real height) held to the part above the cut. */
export function clampToCut(offset: number, cut: number): number {
  return cut > 0 ? Math.min(offset, 1 - cut) : offset
}

/** How tall the last page shows, in pixels, once `cut` of it is left out from its bottom. */
export function cutHeight(height: number, cut: number): number {
  return cut > 0 ? Math.max(0, Math.round(height * (1 - cut))) : height
}
