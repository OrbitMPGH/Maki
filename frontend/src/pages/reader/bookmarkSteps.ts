/**
 * The bookmarked page to jump to from the pages on screen, or null when there is none that way.
 * `shownTo` is the last page on screen, so a bookmark on the second page of a spread is not
 * "next" while that spread is up.
 */
export function adjacentBookmark(
  bookmarks: ReadonlySet<number>,
  page: number,
  shownTo: number,
  direction: 1 | -1,
): number | null {
  let best: number | null = null
  for (const index of bookmarks) {
    if (direction === 1 ? index > shownTo : index < page) {
      if (best === null || (direction === 1 ? index < best : index > best)) best = index
    }
  }
  return best
}
