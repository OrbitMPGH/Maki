/** A clickable external metadata link. `site` is a stable lowercase key (e.g. "mangabaka"). */
export interface MetadataLink {
  site: string
  url: string
}

export interface SeriesDto {
  id: number
  title: string
  sortTitle: string
  originalTitle: string | null
  status: string
  overview: string | null
  year: number | null
  genres: string[]
  monitored: boolean
  monitorNewItems: string
  rootFolderId: number
  folderName: string
  coverUrl: string | null
  totalChapters: number | null
  totalVolumes: number | null
  authorStory: string | null
  authorArt: string | null
  mangaBakaId: number | null
  aniListId: number | null
  malId: number | null
  links: MetadataLink[]
  /** "subChapterSource|wholeChapterSource" when sources disagree on numbering. */
  numberingClash: string | null
  added: string
  chapterCount: number
  chapterFileCount: number
}

export interface MetadataSearchResult {
  providerId: string
  title: string
  coverUrl: string | null
  year: number | null
  status: string
  description: string | null
  totalChapters: number | null
}

export interface RootFolder {
  id: number
  path: string
  freeSpace: number | null
  accessible: boolean
}

export interface ChapterDto {
  id: number
  seriesId: number
  number: number | null
  numberRaw: string | null
  volume: number | null
  title: string | null
  isOneShot: boolean
  language: string
  releaseDate: string | null
  monitored: boolean
  hasFile: boolean
  filePath: string | null
}

export interface QueueItemDto {
  id: number
  chapterId: number
  seriesId: number
  seriesTitle: string
  chapterLabel: string
  sourceName: string
  status: string
  pagesTotal: number
  pagesDone: number
  retryCount: number
  errorMessage: string | null
  queuedAt: string
  completedAt: string | null
}

export interface SourceMappingDto {
  id: number
  seriesId: number
  sourceName: string
  sourceSeriesId: string
  url: string
  languageFilter: string | null
  priority: number
  enabled: boolean
  lastRefresh: string | null
  lastError: string | null
}

export interface AddSeriesRequest {
  metadataProviderId: string
  rootFolderId: number
  monitored: boolean
  monitorNewItems: string
}
