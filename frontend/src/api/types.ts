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
  /** Other primary titles from the provider, for a "show more" expander next to `originalTitle`. */
  altTitles: string[]
  status: string
  /**
   * manga | manhwa | manhua | oel | other, or null on a series whose metadata hasn't been refreshed
   * since the column was added. Picks the reading profile the reader opens with.
   */
  type: string | null
  overview: string | null
  year: number | null
  genres: string[]
  /** Provider-owned tags, replaced on every metadata refresh. Not the user's tags. */
  metadataTags: string[]
  /**
   * "safe" | "suggestive" | "erotica" | "pornographic", or null on a series whose metadata hasn't
   * been refreshed since the column was added.
   */
  contentRating: string | null
  /** Ids of the user-assigned tags on this series; labels and colours come from `useTags()`. */
  tagIds: number[]
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
  kitsuId: number | null
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
  hasAnime: boolean
  animeName: string | null
  animeStart: string | null
  animeEnd: string | null
  /**
   * Downloaded chapters at or below the Rewind read high-water mark (Kavita/scrobble). Null
   * when nothing has reported reading progress for this series yet, distinct from 0 (tracked,
   * but nothing read).
   */
  readChapterCount: number | null
  /**
   * Auto source matching is still queued or running. Add returns before it finishes, so the Sources
   * card shows a spinner off this rather than claiming the series has no sources.
   */
  sourceMatchPending: boolean
  /**
   * "Off" | "ScrobbleOnly" | "Full". ScrobbleOnly withholds tracker pushes only; Full also
   * withholds it from Rewind/reading-history stats.
   */
  incognito: string
  /**
   * Non-fatal problems reported by Add (folder creation, source matching). Absent everywhere else,
   * since the series was still created.
   */
  warnings?: string[] | null
}

/** A user-assigned library label. `color` is a Mantine colour name. */
export interface TagDto {
  id: number
  label: string
  color: string
  seriesCount: number
}

/**
 * The Library grid's filter state. Evaluated client-side; the server stores it verbatim behind a
 * name as a saved filter.
 */
export interface LibraryFilterSpec {
  query?: string | null
  status: string
  tagIds?: number[] | null
  /** "any" | "all": whether a series must carry every listed tag. */
  tagMatch: string
  /** "all" | "monitored" | "unmonitored" */
  monitored: string
  /** "all" | "behind" | "complete" */
  completeness: string
  sort: string
  genres?: string[] | null
  /** "any" | "all" */
  genreMatch: string
  /** Provider-owned tags (`SeriesDto.metadataTags`), not the user's. */
  metadataTags?: string[] | null
  /** "any" | "all" */
  metadataTagMatch: string
  /** Read-percentage window, 0–100. Full range means "don't filter". */
  readMin: number
  readMax: number
  /**
   * `ContentRating` vocabulary values to include, gated by the signed-in user's ceiling. Empty/null
   * means "don't filter" — including series that haven't been refreshed yet (`contentRating: null`).
   */
  contentRatings?: string[] | null
}

export interface SavedFilterDto {
  id: number
  name: string
  spec: LibraryFilterSpec
  sortOrder: number
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
  nextAttempt: string | null
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

export interface ComparePage {
  url: string
  width: number
  height: number
  bytes: number
}

export interface ComparePanel {
  mappingId: number
  sourceName: string
  displayName: string
  status: 'listing' | 'fetching' | 'ready' | 'failed'
  error: string | null
  chapterLabel: string | null
  pages: ComparePage[]
}

/**
 * A source comparison in progress. Panels settle one at a time, so `running` stays true while any
 * of them is still listing or fetching. `mixedChapters` means the sources share no chapter number
 * and each panel is showing its own first chapter instead, which is not a like-for-like comparison.
 */
export interface CompareSnapshot {
  seriesId: number
  running: boolean
  mixedChapters: boolean
  /**
   * Pages were matched across sources by image content rather than shown at their raw indexes, so
   * row N is the same drawing in every column. False when there was only one source to show, or
   * when nothing matched well enough to be worth trusting.
   */
  pagesAligned: boolean
  chapterNumber: number | null
  commonChapters: number[]
  panels: ComparePanel[]
}

export interface AddSeriesRequest {
  metadataProviderId: string
  rootFolderId: number
  monitored: boolean
  monitorNewItems: string
  /**
   * "Off" | "ScrobbleOnly" | "Full". Omitted means "let the server pick from the content rating"
   * (Settings → Library incognito rules), which is what happens on any add form that doesn't ask.
   */
  incognito?: string
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
  updateAvailable: boolean
}

export interface UpdateStatusDto {
  currentVersion: string
  isDevBuild: boolean
  isDocker: boolean
  updateAvailable: boolean
  latestVersion: string | null
  releaseUrl: string | null
  releaseNotes: string | null
  checkedAt: string | null
}

export interface UpdateSettingsDto {
  checkForUpdates: boolean
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
