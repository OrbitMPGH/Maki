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
  /** The user's own rating on a 1–10 scale, or null if unrated. */
  rating: number | null
  mangaBakaId: number | null
  aniListId: number | null
  malId: number | null
  links: MetadataLink[]
  /** "subChapterSource|wholeChapterSource" when sources disagree on numbering. */
  numberingClash: string | null
  added: string
  /** Chapters the user cares about: monitored, plus any already downloaded. */
  chapterCount: number
  chapterFileCount: number
  /** Every chapter known to exist, monitored or not. Denominator fallback when nothing is monitored. */
  knownChapterCount: number
  /** Chapters queued but not yet actively downloading (Queued / RateLimited). */
  queuedCount: number
  /** Chapters actively in the download pipeline (fetching → importing). */
  downloadingCount: number
  /**
   * Non-fatal problems reported by Add (folder creation, source matching). Absent everywhere else
   * — the series was still created.
   */
  warnings?: string[] | null
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
  /** Volume label ("3", "1-2") when the backing file is a volume/compilation CBZ, else null. */
  fileVolume: string | null
}

export interface SeriesFileDto {
  relativePath: string
  fileName: string
  size: number
  sourceName: string | null
  onDisk: boolean
  /** linked | unlinked | unrecognized | missing */
  status: string
  /** What the name parsed to, e.g. "Ch.148", "Vol.3", "Vol.1-2", or null. */
  parsedLabel: string | null
  isVolume: boolean
  /** Chapter numbers this file is linked to (formatted, sorted). */
  mappedChapters: string[]
}

export interface SeriesScrobbleServiceDto {
  service: string
  label: string
  connected: boolean
  remoteId: string | null
  /** library | weblink | derived | search | manual | ignored */
  method: string | null
  url: string | null
  chapter: number
  volume: number
  status: string | null
  syncedAt: string | null
  error: string | null
  /** Set when this series needs review for this tracker. */
  reviewReason: string | null
  reviewCandidates: { id: string; title: string; url: string }[]
}

export interface SeriesScrobbleDto {
  configured: boolean
  matched: boolean
  kavitaSeriesId: number | null
  services: SeriesScrobbleServiceDto[]
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

export interface QueueHistoryDto {
  items: QueueItemDto[]
  total: number
  page: number
  pageSize: number
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

export type NotificationType = 'Discord' | 'Webhook'

export interface NotificationConfig {
  webhookUrl: string | null
  url: string | null
  bearerToken: string | null
}

export interface NotificationEvents {
  chapterDownloaded: boolean
  downloadFailed: boolean
  newChapterAvailable: boolean
  importCompleted: boolean
  healthIssue: boolean
}

export interface NotificationDto {
  id: number
  name: string
  type: NotificationType
  enabled: boolean
  config: NotificationConfig
  events: NotificationEvents
}

export interface NotificationRequest {
  name: string
  type: NotificationType
  enabled: boolean
  config: NotificationConfig
  events: NotificationEvents
}
