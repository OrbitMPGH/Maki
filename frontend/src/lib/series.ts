import type { SeriesDto } from '../api/types'

export const isArchived = (s: Pick<SeriesDto, 'archivedAt'>): boolean => s.archivedAt != null

/** The library as a reader sees it by default: archived series are left out. */
export function activeSeries<T extends Pick<SeriesDto, 'archivedAt'>>(list: T[]): T[] {
  return list.filter((s) => !isArchived(s))
}
