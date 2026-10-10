import type { QualityTierName, UpgradeQueueInfoDto } from './upgrades'

/** A clickable external metadata link. `site` is a stable lowercase key (e.g. "mangabaka"). */
export interface MetadataLink {
  site: string
  url: string
}

/** A series title plus the language it is written in. A null `language` means the provider didn't say. */
export interface LocalizedTitle {
  title: string
  /** Lowercase code as the provider spelled it: "en", "ja", sometimes regional ("pt-br"). */
  language: string | null
}

export interface SeriesDto {
  id: number
  /**
   * The canonical title, what the folder on disk, the file names and `sortTitle` are built from.
   * Always the provider's English title when there is one. Render `displayTitle` instead.
   */
  title: string
  /**
   * `title` resolved against this user's title-language preference, falling back to `title`. Never
   * null, so it can be rendered unconditionally; equal to `title` in the default configuration.
   */
  displayTitle: string
  sortTitle: string
  originalTitle: string | null
  /**
   * Other primary titles from the provider with the language each is written in, for the
   * "show more" expander next to `originalTitle` and as the pool `displayTitle` comes from.
   */
  altTitles: LocalizedTitle[]
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
  /** Full on-disk path to the series folder, admin-only; null otherwise. */
  rootFolderPath: string | null
  coverUrl: string | null
  totalChapters: number | null
  totalVolumes: number | null
  authorStory: string | null
  authorArt: string | null
  publisher: string | null
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
  /** Chapters the user asked for, plus any already downloaded. The progress denominator. */
  wantedChapterCount: number
  chapterFileCount: number
  /** Chapters whose file Maki deleted on purpose. Counted in `wantedChapterCount`, never missing. */
  removedChapterCount: number
  /** "Default" | "On" | "Off": this series' choice about deleting read chapter files. */
  readFileCleanup: string
  /** Whether read files are actually being deleted, the choice and the instance switch combined. Detail endpoint only. */
  readFileCleanupActive: boolean
  /** Every chapter known to exist, wanted or not. Denominator fallback when nothing is wanted. */
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
   * Downloaded chapters the caller has finished, counted from their completed progress rows
   * (built-in reader and Kavita sync alike). Null when no progress exists for this series yet,
   * distinct from 0 (tracked, but nothing read).
   */
  readChapterCount: number | null
  /**
   * Auto source matching is still queued or running. Add returns before it finishes, so the Sources
   * card shows a spinner off this rather than claiming the series has no sources.
   */
  sourceMatchPending: boolean
  /** A library import's files are registered but wait for the background match to be linked. */
  importLinkPending: boolean
  /**
   * "Off" | "ScrobbleOnly" | "Full". ScrobbleOnly withholds tracker pushes only; Full also
   * withholds it from Rewind/reading-history stats.
   */
  incognito: string
  /**
   * "Default" | "All" | "Reading" | "Muted", how loudly *you* want to hear about this series.
   * Per-user like `rating`, so two readers see different values for the same series, and "Default"
   * defers to the `seriesDefault` on your inbox prefs.
   */
  notificationMode: string
  /** Personal time left from comparable timed chapters in the built-in reader. Detail endpoint only. */
  readTimeEstimate?: {
    seconds: number
    remainingChapters: number
    style: 'scrolling' | 'paged'
    sampleChapters: number
    seriesSpecific: boolean
  } | null
  /**
   * Source keys linked to this series, enabled or not. Only the library list endpoint fills these
   * three in, elsewhere they come back empty, which means "not loaded", not "none linked".
   */
  sources?: string[]
  /** The subset of `sources` that actually runs: mapping enabled and source not globally off. */
  enabledSources?: string[]
  /** Sources the downloaded files came from. Survives the mapping being removed or switched off. */
  fileSources?: string[]
  /**
   * Non-fatal problems reported by Add (folder creation, source matching). Absent everywhere else,
   * since the series was still created.
   */
  warnings?: string[] | null
  /** Upgrade profile pinned to this series, or null to fall back to the instance default. */
  upgradeProfileId: number | null
  /**
   * Null when this series has never been scanned for upgrades, by any of the three entry points.
   * `checked` is null for scans recorded before it was kept; `skipped` maps a reason code to a count.
   */
  lastUpgradeScan: {
    at: string
    probed: number
    queued: number
    checked: number | null
    skipped: Record<string, number>
  } | null
  /** Id of the series' pending torrent volume proposal, if any. */
  pendingProposalId: number | null
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
  /** `SeriesDto.type` values to include. Empty/null means "don't filter"; untyped series only show then. */
  types?: string[] | null
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
  /** Chapter-count window; null on either end means unbounded. `chapterMode` picks which count. */
  chapterMin?: number | null
  chapterMax?: number | null
  /** "downloaded" (files on disk) | "total" (the denominator the cards show). */
  chapterMode: string
  /**
   * `ContentRating` vocabulary values to include, gated by the signed-in user's ceiling. Empty/null
   * means "don't filter", including series that haven't been refreshed yet (`contentRating: null`).
   */
  contentRatings?: string[] | null
  /** Source keys the series must be linked to (`SeriesDto.sources`). */
  sources?: string[] | null
  /** "any" | "all" */
  sourceMatch: string
  /**
   * "all" | "none" (nothing linked) | "hasEnabled" | "noneEnabled" (linked, nothing live) |
   * "hasDisabled" (at least one linked source switched off).
   */
  sourceState: string
  /** Source keys the downloaded files came from (`SeriesDto.fileSources`). */
  fileSources?: string[] | null
  /** "any" | "all" */
  fileSourceMatch: string
  /** "all", "default" (no pin of its own), or a quality profile id as a string. Absent on older presets. */
  qualityProfile?: string
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

/** A file's release tier and archive stats, as the quality backfill measures it. */
export interface ChapterFileQualityDto {
  /** The backing `ChapterFile`'s id, for the per-file actions such as `POST /chapter-files/{id}/trusted`. */
  fileId: number
  tier: 'unknown' | 'aggregator' | 'scanlator' | 'official' | 'volume'
  group: string | null
  pageCount: number | null
  medianWidth: number | null
  medianHeight: number | null
  /** jpg | png | webp | avif | gif | pdf | mixed | unknown */
  imageFormat: string | null
  /** False until the backfill has actually opened the archive; the fields above are guesses until then. */
  measured: boolean
  /** Quality score against the series' upgrade profile. Null with no profile or an unmeasured file. */
  score: number | null
  /** Whether this file already meets its profile's cutoff. Null with no profile or an unmeasured file. */
  cutoffMet: boolean | null
  /** "Protect from upgrades": a trusted file is never replaced by the automatic upgrader. */
  trusted: boolean
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
  /** Whether the user wants this chapter. Nothing in the download pipeline writes it. */
  wanted: boolean
  hasFile: boolean
  /** When Maki deleted this chapter's file on purpose; null while it has a file. Not missing, never re-queued on its own. */
  fileRemovedAt: string | null
  /** When read file cleanup will delete this chapter's file, or null when it isn't scheduled. */
  fileDeleteDueAt: string | null
  filePath: string | null
  /**
   * Where the file came from: a registered source's name, the literal "import" for a file brought
   * in from disk, or "torrent:{indexer}" for a grabbed release. Null when there is no file. Only
   * the first kind is ever replaced by a source-switch re-download.
   */
  fileSourceName: string | null
  /** Original torrent/usenet release name, kept after renaming. Null for non-torrent files. */
  fileReleaseName: string | null
  /** Volume label ("3", "1-2") when the backing file is a volume/compilation CBZ, else null. */
  fileVolume: string | null
  /** Null when there is no file. */
  fileQuality: ChapterFileQualityDto | null
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
  /** Null for a file with no ChapterFile record (unlinked, unrecognized). */
  quality: ChapterFileQualityDto | null
}

/** The series page's Files tab count and unlinked banner, without the full listing. */
export interface SeriesFilesSummaryDto {
  count: number
  unlinkedOnDisk: number
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

/**
 * Mirrors `DownloadOrigin` on the server: the enum name, fully lowercased (not camelCase, since this
 * is `.ToString().ToLowerInvariant()`, not the camelCase convention most other wire enums use). An
 * origin this build doesn't recognise (a newer server, older page) reads as 'unknown' rather than
 * breaking a lookup table; see `queueOriginOrUnknown` in `api/queue.ts`.
 */
export type QueueOrigin =
  | 'unknown'
  | 'manual'
  | 'smartdownload'
  | 'monitorrefresh'
  | 'requestapproval'
  | 'healthrepair'
  | 'upgrade'

/**
 * Nothing here is a sentence in a language. The label and the failure reason arrive as their parts,
 * because one queue update is broadcast to every connected client at once and they do not share a
 * language. `api/queue.ts` words both.
 */
export interface QueueItemDto {
  id: number
  chapterId: number
  seriesId: number
  seriesTitle: string
  /** The release title, for a series-level torrent grab. Null for a per-chapter scraper item. */
  releaseTitle: string | null
  /** The chapter's own title, for a one-shot or an unnumbered chapter. */
  chapterTitle: string | null
  chapterVolume: number | null
  /** A string, not a number: an identifier that has to match the one on disk exactly. */
  chapterNumber: string | null
  sourceName: string
  status: string
  pagesTotal: number
  pagesDone: number
  retryCount: number
  nextAttempt: string | null
  /** An `error.download.*` catalogue key, or null when the reason is not Maki's own words. */
  errorKey: string | null
  errorParams: Record<string, unknown> | null
  /** Text from outside Maki, or English from before the queue was keyed. Shown when `errorKey` is null. */
  errorMessage: string | null
  queuedAt: string
  completedAt: string | null
  origin: QueueOrigin
  /** Set only when `origin` is 'upgrade'. */
  upgrade: UpgradeQueueInfoDto | null
}

export interface QueueHistoryDto {
  items: QueueItemDto[]
  total: number
  page: number
  pageSize: number
}

/** Whole-queue counts for the Activity badge. `active` excludes failed and parked items. */
export interface QueueSummaryDto {
  active: number
  awaitingImport: number
  failed: number
}

/** Failed queue rows sharing a failure key and a source. `reasonKey` is null when the reason is another program's own text. */
export interface QueueFailureGroupDto {
  reasonKey: string | null
  sourceName: string
  count: number
}

/** Which Failed rows a bulk retry or remove acts on; both filters unset means all of them. */
export interface QueueFailedAction {
  reason?: string
  source?: string
  /** Remove only: also stop the volume search grabbing the removed torrent releases again. */
  blockReleases?: boolean
}

/** An existing library file a downloaded file would leave backing nothing. */
export interface ImportPlanExistingDto {
  chapterFileId: number
  relativePath: string
  size: number
  chapters: string[]
}

export interface ImportPlanFileDto {
  fileName: string
  size: number
  label: string | null
  chapters: string[]
  newChapters: string[]
  replaces: ImportPlanExistingDto[]
  /** Only meaningful when the plan `isUpgrade`. */
  upgradeCount: number
  alreadyMetCount: number
}

export interface TorrentImportPlanDto {
  queueItemId: number
  seriesId: number
  seriesTitle: string
  releaseName: string
  files: ImportPlanFileDto[]
  error: string | null
  hasConflicts: boolean
  newChapterCount: number
  replacedFileCount: number
  /** True when the download came from the upgrader or a proposal; drives the skip checkboxes. */
  isUpgrade: boolean
  /** File names whose chapters are all at cutoff already; pre-ticked as Skip when `isUpgrade`. */
  suggestedSkips: string[]
}

/** Matches the server's `ImportDecision`; sent verbatim. */
export type ImportDecision = 'Replace' | 'SkipExisting' | 'Reject'

export interface ImportDecisionResultDto {
  imported: number
  linked: number
  skipped: number
  deleted: number
}

export interface SourceMappingDto {
  id: number
  seriesId: number
  sourceName: string
  sourceSeriesId: string
  url: string
  /**
   * Ordered comma-separated language codes ("en,es"), or null for the source default (English).
   * Only honoured by sources whose `supportsLanguageFilter` is true. Each extra language adds its
   * own chapter row per number, because chapter identity is (number, language).
   */
  languageFilter: string | null
  priority: number
  enabled: boolean
  lastRefresh: string | null
  lastError: string | null
  /** Null on upgraded or newly linked mappings until their first successful chapter refresh. */
  chapterSnapshotAt: string | null
  origin: 'Unknown' | 'TitleSearch' | 'CrossId' | 'Manual'
}

export interface ComparePage {
  url: string
  /** Null when the format couldn't be measured (AVIF); the image still displays. */
  width: number | null
  height: number | null
  bytes: number
}

/**
 * A column's quality read against the series' upgrade profile, filled by `SourceMappingController`
 * from the same pages the compare job already sampled (no extra probe). Null on a panel where no
 * page width could be measured at all.
 */
export interface ComparePanelQualityDto {
  tier: QualityTierName
  score: number
  matchedFormats: string[]
  isUpgrade: boolean
  /** A reason code from `UPGRADE_REASON_LABELS`, set whenever `isUpgrade` is false. */
  reason: string | null
  medianWidth: number | null
  pageCount: number | null
  /** The file on disk's tier, score and width: the same on every panel for one chapter, null with no file. */
  currentTier: QualityTierName | null
  currentScore: number | null
  currentWidth: number | null
  /** Parts of `score` from the profile's resolution and compression weights. */
  resolutionPoints: number
  compressionPoints: number
  /** JPG-equivalent bits per pixel of the sampled pages, null when they could not be measured. */
  bitsPerPixel: number | null
  currentResolutionPoints: number | null
  currentCompressionPoints: number | null
  currentBitsPerPixel: number | null
}

export interface ComparePanel {
  mappingId: number
  sourceName: string
  displayName: string
  status: 'listing' | 'fetching' | 'ready' | 'failed'
  error: string | null
  chapterLabel: string | null
  /**
   * This source's pages were matched against the others' by image content. False for a source
   * carrying a different edition, whose column is shown for ranking but lines up with nothing.
   */
  aligned: boolean
  /** One entry per grid row, null where this source has no page for that row. */
  pages: (ComparePage | null)[]
  /** Pages in the whole chapter, not just the sampled rows. Null when the source didn't say. */
  pageCount: number | null
  quality: ComparePanelQualityDto | null
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
  /** Omitted follows the "Default monitoring for new series" setting. */
  monitorNewItems?: string
  /**
   * "Off" | "ScrobbleOnly" | "Full". Omitted means "let the server pick from the content rating"
   * (Settings → Library incognito rules), which is what happens on any add form that doesn't ask.
   */
  incognito?: string
  addedFrom?: string
  clientMutationId?: string
  /** Upgrade profile to pin, or null/omitted for the instance default. */
  upgradeProfileId?: number | null
}

export type NotificationType =
  | 'Discord'
  | 'Webhook'
  | 'Telegram'
  | 'Notifiarr'
  | 'Ntfy'
  | 'Gotify'
  | 'Pushover'
  | 'Apprise'
  | 'SlackWebhook'

/** Flat string map keyed by the provider descriptor's field keys. */
export type NotificationConfig = Record<string, string>

export type NotificationFieldKind = 'Text' | 'Secret' | 'Url' | 'Number' | 'Boolean'

export interface NotificationFieldDescriptor {
  key: string
  kind: NotificationFieldKind
  required: boolean
  placeholder: string | null
  min?: number | null
  max?: number | null
}

export interface NotificationProviderDescriptor {
  type: NotificationType
  fields: NotificationFieldDescriptor[]
  supportsPoster: boolean
}

export interface NotificationEvents {
  chapterDownloaded: boolean
  downloadFailed: boolean
  newChapterAvailable: boolean
  importCompleted: boolean
  healthIssue: boolean
  updateAvailable: boolean
  seriesAdded: boolean
  seriesRemoved: boolean
  requestSubmitted: boolean
  requestResolved: boolean
  manualMatchNeeded: boolean
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
  /** Series-scoped events only fire for series carrying one of these tags. Empty means every series. */
  tagIds: number[]
}

export interface NotificationRequest {
  name: string
  type: NotificationType
  enabled: boolean
  config: NotificationConfig
  events: NotificationEvents
  tagIds: number[]
}

/** Matches the server's `ScrobbleStatus` vocabulary for an import-list-eligible entry. */
export type ImportListStatus = 'Reading' | 'PlanToRead' | 'Completed'

export interface ImportListTrackerPrefs {
  enabled: boolean
  statuses: ImportListStatus[]
  rootFolderId: number | null
  monitored: boolean
  monitorNewItems: string
  maxPerRun: number
}

export interface ImportListRunSummary {
  at: string
  added: number
  requested: number
  skipped: number
  errors: number
  /** Nothing ran because the local MangaBaka database is not downloaded yet. */
  dumpUnavailable: boolean
}

export interface ImportListTrackerDto {
  service: string
  label: string
  connected: boolean
  prefs: ImportListTrackerPrefs
  lastRun: ImportListRunSummary | null
}

/** Unmatched | Removed | Ignored | Added. */
export type ImportListSkipReason = 'Unmatched' | 'Removed' | 'Ignored' | 'Added'

export interface ImportListSkipDto {
  id: number
  service: string
  remoteId: string
  title: string
  reason: ImportListSkipReason
  createdAt: string
}

export interface ImportListsStatusDto {
  enabled: boolean
  intervalMinutes: number
  trackers: ImportListTrackerDto[]
  skipped: ImportListSkipDto[]
}

/**
 * `POST /importlists/run` reply. Every manual run kicks off in the background and answers 202 with
 * `started: true`; the outcome arrives later via the inbox and `ImportListTrackerDto.lastRun`.
 */
export type ImportListRunResponse = { started: true }
