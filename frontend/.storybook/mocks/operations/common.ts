import type { SourceInfo } from '../../../src/api/hooks'
import type { MetadataSearchResult, RootFolder } from '../../../src/api/types'
import { cover } from '../api'
import { seriesById } from '../shared'

const source = (name: string, displayName: string, extra: Partial<SourceInfo> = {}): SourceInfo => ({
  name,
  displayName,
  baseUrl: `https://${name}.example`,
  needsFlareSolverr: false,
  supportsLanguageFilter: false,
  supportedLanguages: ['en'],
  enabled: true,
  ...extra,
})

export const sources: SourceInfo[] = [
  source('mangadex', 'MangaDex', { supportsLanguageFilter: true, supportedLanguages: ['en', 'es', 'pt-br'] }),
  source('asura', 'Asura Scans'),
  source('comick', 'Comick', { needsFlareSolverr: true }),
  source('weebcentral', 'Weeb Central'),
]

export const rootFolders: RootFolder[] = [
  { id: 1, path: '/library/manga', freeSpace: 842_000_000_000, accessible: true },
  { id: 2, path: '/library/manhwa', freeSpace: 311_000_000_000, accessible: true },
]

export const title = (seriesId: number) => seriesById(seriesId).title

/** A metadata search hit shaped like the real endpoint's, with a generated cover. */
export const metadataHit = (
  providerId: string,
  hitTitle: string,
  year: number | null,
  status = 'Ongoing',
  totalChapters: number | null = null,
): MetadataSearchResult => ({
  providerId,
  title: hitTitle,
  coverUrl: cover(hitTitle),
  year,
  status,
  description: null,
  totalChapters,
})
