import { useMatches } from '@mantine/core'

/** A catalogue rail's length, as `DiscoverService.RailSize` builds it. */
export const CATALOGUE_PAGE = 40

/**
 * Cuts a full page of titles to whole rows at the current column count, so the grid never ends on
 * a part row. A list shorter than `page` is everything there is, so it stays whole.
 */
export function useWholeRows<T>(items: T[], cols: Record<string, number>, page: number): T[] {
  const columns = useMatches(cols)
  if (items.length < page || items.length <= columns) return items
  return items.slice(0, items.length - (items.length % columns))
}
