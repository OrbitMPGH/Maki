import { api, apiUpload } from './client'
import type { MetadataSearchResult } from './types'

/** One favourite manga of a Mihon backup (`MihonParsedManga`). */
export interface MihonParsedManga {
  /** Position in the backup; stable for the run. */
  index: number
  title: string
  author: string | null
  /** Category names; empty for Mihon's implicit Default. */
  categories: string[]
  aniListId: number | null
  malId: number | null
  kitsuId: number | null
  mangaBakaId: string | null
  readNumbers: number[]
  /** Only set when the manga has no chapter rows. */
  trackerLastRead: number | null
  chapterCount: number
  /** Includes unnumbered reads. */
  readCount: number
}

export interface MihonParseResponse {
  manga: MihonParsedManga[]
  skippedNonLibrary: number
  categoryCount: number
}

export interface MihonResolveItem {
  index: number
  title: string
  author: string | null
  aniListId: number | null
  malId: number | null
  kitsuId: number | null
  mangaBakaId: string | null
}

export type MihonMatchStatus = 'matched' | 'needsReview' | 'notFound'
export type MihonMatchedBy = 'mangabaka' | 'anilist' | 'mal' | 'kitsu' | 'title'

export interface MihonResolveResult {
  index: number
  status: MihonMatchStatus
  matchedBy: MihonMatchedBy | null
  match: MetadataSearchResult | null
  candidates: MetadataSearchResult[]
  /** For `match` only. */
  library: 'none' | 'visible' | 'elsewhere'
  seriesId: number | null
}

export interface MihonImportItem {
  providerId: string
  categories: string[]
  readNumbers: number[]
  trackerLastRead: number | null
}

export interface MihonImportRequest {
  runId: string
  rootFolderId: number
  monitored: boolean
  monitorNewItems: string
  applyTags: boolean
  applyReadState: boolean
  items: MihonImportItem[]
}

export interface MihonImportResult {
  providerId: string
  outcome: 'created' | 'existing' | 'skipped' | 'failed'
  seriesId: number | null
  /** Localized by the server. */
  reason: string | null
}

export function parseMihonBackup(file: File): Promise<MihonParseResponse> {
  const form = new FormData()
  form.append('file', file)
  return apiUpload<MihonParseResponse>('/mihonimport/parse', form)
}

export async function resolveMihon(items: MihonResolveItem[]): Promise<MihonResolveResult[]> {
  const res = await api<{ results: MihonResolveResult[] }>('/mihonimport/resolve', {
    method: 'POST',
    body: JSON.stringify({ items }),
  })
  return res.results
}

export async function importMihon(body: MihonImportRequest): Promise<MihonImportResult[]> {
  const res = await api<{ results: MihonImportResult[] }>('/mihonimport/import', {
    method: 'POST',
    body: JSON.stringify(body),
  })
  return res.results
}
