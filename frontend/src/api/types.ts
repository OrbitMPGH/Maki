import type { QualityTierName, UpgradeQueueInfoDto } from './upgrades'

/** A clickable external metadata link. `site` is a stable lowercase key (e.g. "mangabaka"). */
export interface MetadataLink {
  site: string
  url: string
  /** The site's own score as it writes it (8.4, 86), when the response carries one. */
  score?: number | string | null
  votes?: number | null
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
   * Not on library list rows, which carry `altTitleNames` instead.
   */
  altTitles?: LocalizedTitle[]
  /** The same titles as bare names, for title search. Library list rows only. */
  altTitleNames?: string[]
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
  /** The user's "so far" score while partway through, or null. Never set alongside `rating`. Absent on older responses. */
  provisionalRating?: number | null
  /** The highest chapter number read when `provisionalRating` was set. */
  provisionalAtChapter?: number | null
  /** You finished this series. The stars set a "so far" score only while it is part-read. */
  finished?: boolean
  /** The "how is it so far?" prompt waiting for you. Detail endpoint only. */
  ratingCheckpoint?: 'hooked' | 'caughtUp' | 'volumeEnd' | null
  mangaBakaId: number | null
  aniListId: number | null
  malId: number | null
  kitsuId: number | null
  /** Not on library list rows. */
  links?: MetadataLink[]
  /** "subChapterSource|wholeChapterSource" when sources disagree on numbering. */
  numberingClash: string | null
  added: string
  /** Chapters the user asked for, plus any already downloaded. The progress denominator. */
  wantedChapterCount: number
  chapterFileCount: number
  /** Chapters whose file Maki deleted on purpose. Counted in `wantedChapterCount`, never missing. */
  removedChapterCount: number
  /**
   * The one total Reading and Downloads count against: the locked or MangaBaka total (raised to the
   * highest whole chapter listed), plus listed specials only while `specialsCounted`. Zero on
   * responses that carry no counts, which fall back to the wanted total.
   */
  progressTotal?: number
  /** Chapters had (on disk or removed on purpose) within `progressTotal`'s scope. */
  progressHave?: number
  /** `readChapterCount` within `progressTotal`'s scope; null when nothing has been read. */
  progressRead?: number | null
  /** Specials count: neither the global "unmonitor specials" setting nor Main only mode is on. */
  specialsCounted?: boolean
  /** Listed chapters with a decimal number. */
  listedSpecials?: number
  /** "Default" | "On" | "Off": this series' choice about deleting read chapter files. */
  readFileCleanup: string
  /** Whether read files are actually being deleted, the choice and the instance switch combined. Detail endpoint only. */
  readFileCleanupActive: boolean
  /** "Default" | "Never" | "Smallest" | "Compact" | "Balanced" | "High" | "Lossless": this series' page conversion choice. */
  pageConversion: string
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
  /** The signed-in user's own status on this series. */
  readingStatus: 'PlanToRead' | 'Reading' | 'OnHold' | 'Dropped'
  /** True when `readingStatus` was set by one of the auto rules rather than by the user. */
  readingStatusAuto: boolean
  /** Your "Prepare chapters ahead" choice for this series; null while the instance has it off. Detail endpoint only. */
  prepareAhead?: SeriesPrepareAhead | null
  /** When the series was archived out of the active library, or null while it is active. */
  archivedAt: string | null
  archivedByUserId: number | null
  /** The "finished, archive it?" hint applies to you. Detail endpoint only; false without ArchiveSeries or once dismissed. */
  archiveHintEligible?: boolean
  /** You finished this series and have not rated it or dismissed the ask. Detail endpoint only. */
  rateHintEligible?: boolean
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
  /** When you last read a chapter of this series, or null if never. Library list endpoint only. */
  lastReadAt?: string | null
  /** When the newest chapter file landed, or null with no files. Library list endpoint only. */
  lastDownloadedAt?: string | null
  /** The newest release date among the series' known chapters, or null with none dated. Library list endpoint only. */
  latestChapterAt?: string | null
  /** MangaBaka's aggregate score, 0–10, from the local dump. Library list endpoint only. */
  communityRating?: number | null
  /** Bytes of every chapter file on disk, or null with none. Library list endpoint only. */
  sizeOnDisk?: number | null
  /**
   * When the monitored refresh next syncs this series; a time already past means it is due. Null
   * when it will not be synced at all. Library list endpoint only.
   */
  nextCheckAt?: string | null
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
  /** What volume first holds back from automatic source downloads, while anything is held. */
  volumeHold?: VolumeHold | null
  /** Metadata fields set by hand, which a refresh leaves alone. See `MetadataField`. */
  lockedFields: MetadataField[]
}

export interface VolumeHold {
  reason: 'searching' | 'proposal' | 'downloading'
  /** The highest chapter number held; null holds every chapter. */
  through: number | null
  proposalId: number | null
  /** When the hold lapses on its own; null while the search runs. */
  until: string | null
}

/** The camelCase names of `SeriesMetadataField` on the server. */
export type MetadataField =
  | 'title'
  | 'overview'
  | 'status'
  | 'totalChapters'
  | 'totalVolumes'
  | 'genres'
  | 'cover'
  | 'year'
  | 'authorStory'
  | 'authorArt'
  | 'publisher'
  | 'altTitles'
  | 'sortTitle'
  | 'type'
  | 'contentRating'

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
  /** User tag ids a series must not carry. */
  excludeTagIds?: number[] | null
  /** Genres a series must not carry. */
  excludeGenres?: string[] | null
  /** Only series with none of the user's own tags. */
  untaggedOnly?: boolean
  /** "asc" | "desc" to override the sort key's natural direction; empty or absent keeps it. */
  sortDir?: string
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
  /** "all" | "rated" | "unrated" on the user's own rating. Absent on older presets. */
  ratingState?: string
  /** 1–10 window on the user's own rating, only applied when `ratingState` is "rated". */
  ratingMin?: number
  ratingMax?: number
  /** 0–10 window on the MangaBaka score. Full range means "don't filter"; narrower drops unscored series. */
  communityRatingMin?: number
  communityRatingMax?: number
  /** `SeriesDto.readingStatus` values to include. Empty means any. */
  readingStatuses: string[]
  /** First-year window; null on either end is unbounded. Series with no year drop out once either is set. Absent on older presets. */
  yearMin?: number | null
  yearMax?: number | null
  /** Story or art credits, any one of which the series must carry. */
  credits?: string[] | null
  publishers?: string[] | null
  rootFolderIds?: number[] | null
  /** "all" | "on" | "off" */
  incognito?: string
  /** "all" | "muted" | "unmuted", on your own notification mode. */
  muted?: string
  /** "all" | "has" | "none" */
  anime?: string
  /** "all" | "clash" | "none" */
  numberingClash?: string
  /** "readCleanup" and/or "pageConversion": series with a setting of their own. */
  overrides?: string[] | null
  /** Only series added within this many days; null for any time. */
  addedWithinDays?: number | null
  /** "none" | "status" | "tag" | "rootFolder" | "type": how the view is grouped. Not a filter. Absent on older presets. */
  groupBy?: string
}

export interface SavedFilterDto {
  id: number
  name: string
  spec: LibraryFilterSpec
  sortOrder: number
  /** The preset the Library opens with when nothing is remembered. At most one per user. */
  isDefault: boolean
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
  /** Downloads into this folder wait while free space is below this. Null is the default, 0 never holds. */
  minFreeBytes?: number | null
  defaultMinFreeBytes?: number
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
  /** The removal was read file cleanup's, which also turned Wanted off. False while the chapter has a file. */
  fileAutoRemoved: boolean
  /** The last health scan found this chapter's file gone from disk. It still has a file record, so `hasFile` stays true. */
  fileMissing: boolean
  /** When read file cleanup will delete this chapter's file, or null when it isn't scheduled. */
  fileDeleteDueAt: string | null
  /** When the last enabled source that listed this chapter stopped listing it. Null while one does, or if none ever did. */
  delistedAt: string | null
  filePath: string | null
  /**
   * Where the file came from: a registered source's name, the literal "import" for a file brought
   * in from disk, or "torrent:{indexer}" for a grabbed release. Null when there is no file. Only
   * the first kind is ever replaced by a source-switch re-download.
   */
  fileSourceName: string | null
  /** Original torrent release name, kept after renaming. Null for non-torrent files. */
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
  /** Null for a series-level torrent grab. */
  chapterId: number | null
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
  seriesCoverUrl: string | null
  /** Claim class: every 'high' row is taken before any 'normal' one, and those before 'low'. */
  priority?: QueuePriority
  /** The series' root folder, which a low disk space hold is keyed by. */
  rootFolderId?: number
}

export type QueuePriority = 'high' | 'normal' | 'low'

export interface QueueHistoryDto {
  items: QueueItemDto[]
  total: number
  page: number
  pageSize: number
}

/** One history run: a single row, or one series' burst of the same status, newest first. */
export interface QueueHistoryEntryDto {
  items: QueueItemDto[]
}

/** `total` counts entries, `itemTotal` the rows inside them. */
export interface QueueHistoryGroupsDto {
  entries: QueueHistoryEntryDto[]
  total: number
  itemTotal: number
  page: number
  pageSize: number
}

export type QueueHistoryStatusFilter = 'all' | 'completed' | 'cancelled' | 'upgrades'

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

/** `until` is when the pause lifts by itself (UTC), or null for "until somebody resumes". */
export interface QueuePauseEntry {
  until: string | null
}

/** What is paused on scraper downloads. Torrents already handed to the download client are never part of it. */
export interface QueuePauseDto {
  all: QueuePauseEntry | null
  sources: { sourceName: string; until: string | null }[]
  /** Series paused on their own. Missing from a server older than series pauses. */
  series?: { seriesId: number; title: string; until: string | null }[]
  /** Root folders below their free space threshold. These lift by themselves when space returns. */
  diskHolds?: QueueDiskHold[]
}

export interface QueueDiskHold {
  rootFolderId: number
  path: string
  freeBytes: number
  minFreeBytes: number
}

/** Rough time left, in seconds. Null wherever there is too little download history to say. */
export interface QueueEtaDto {
  queueSeconds: number | null
  series: { seriesId: number; seconds: number | null; held: boolean }[]
  held: boolean
}

/** `source` unset pauses every scraper download; `resumeAt` unset pauses until resumed. */
export interface QueuePauseRequest {
  source?: string
  resumeAt?: string
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
  /** Sent back with the decision, so an import reviewed under other name parsing settings is refused. */
  parsingFingerprint: string
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
  /** Every chapter of the series in reading order, one per label, for the review's chapter map. */
  chapters: ImportPlanChapterDto[]
}

export interface ImportPlanChapterDto {
  label: string
  number: number | null
  /** Every row with this label is backed by a file today. */
  hasFile: boolean
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

/** The best result a source search returned that fell just short of an automatic match. */
export interface SourceNearMissDto {
  id: number
  sourceName: string
  sourceSeriesId: string
  title: string
  url: string
  coverUrl: string | null
  author: string | null
  language: string | null
  description: string | null
  score: number
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

export interface ArchiveSeriesRequest {
  removeFiles: boolean
  /** Set once the server said the files cannot go to the recycle bin and the user chose to delete them. */
  permanent?: boolean
}

export interface ArchiveSeriesResponse {
  deleted: number
  failed: number
}

export interface BulkArchiveSeriesRequest extends ArchiveSeriesRequest {
  seriesIds: number[]
}

export interface BulkArchiveSeriesResult {
  seriesId: number
  ok: boolean
  notFound: boolean
  /** Catalogue key of the refusal, e.g. error.series.archived. */
  code: string | null
  error: string | null
  /** The recycle bin refused this one's files; archiving it again with permanent: true would work. */
  permanentDeleteAvailable: boolean
  deleted: number
  failed: number
}

export interface BulkArchiveSeriesResponse {
  results: BulkArchiveSeriesResult[]
}

export interface RestoreSeriesRequest {
  /** Queue the chapters whose files were removed for download again. */
  redownloadRemoved: boolean
}

export interface RestoreSeriesResponse {
  queued: number
  /** Localized reason the removed chapters could not be queued, or null. */
  queueError: string | null
}

/** GET series/{id}/archive-preview: what the archive and restore dialogs show. */
export interface ArchivePreview {
  /** Bytes removing the series' files would free, leaving out hardlinked files. */
  freeableBytes: number
  /** Other users who read the series in the last 30 days and have not finished it. */
  otherActiveReaders: number
  /** Chapters a restore with redownload would queue: removed, no file, still wanted. */
  redownloadableChapters: number
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
  seriesArchived: boolean
  requestSubmitted: boolean
  requestResolved: boolean
  manualMatchNeeded: boolean
  seriesStatusChanged: boolean
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
  /** How this instance was started; the desktop build updates itself through its tray app. */
  runtime: HostRuntime
  /** Desktop only: the tray app has the update downloaded, so a restart installs it. */
  updateReady: boolean
}

export type HostRuntime = 'docker' | 'desktop' | 'bare'

export interface HostInfoDto {
  runtime: HostRuntime
  /** Something starts Maki again after it stops: Docker, the desktop tray app or systemd. */
  supervised: boolean
  port: number
  bindAddress: string
  configuredBindAddress: string
  networkAccess: boolean
  /** Differs from networkAccess while a change waits for a restart. */
  configuredNetworkAccess: boolean
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
  lastAttemptAt: string | null
  lastSuccessAt: string | null
  /** A short code, never provider text: `status:404`, `timeout`, `network` or `error`. */
  lastError: string | null
  consecutiveFailures: number
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
  shortlisted: number
  skipped: number
  errors: number
  /** Nothing ran because the local MangaBaka database is not downloaded yet. */
  dumpUnavailable: boolean
  /** The run stopped shortlisting Plan to read entries because the shortlist is full. */
  shortlistFull: boolean
}

export interface ImportListTrackerDto {
  service: string
  label: string
  connected: boolean
  prefs: ImportListTrackerPrefs
  lastRun: ImportListRunSummary | null
}

/** Unmatched | Removed | Ignored | Added | Shortlisted. */
export type ImportListSkipReason = 'Unmatched' | 'Removed' | 'Ignored' | 'Added' | 'Shortlisted'

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

/**
 * How many chapters of a series the server keeps upscaled ahead for you. `chapters` null follows the
 * instance setting (`defaultChapters` while read within `days` days), 0 never, a number keeps that many.
 */
export interface SeriesPrepareAhead {
  chapters: number | null
  defaultChapters: number
  days: number
}
