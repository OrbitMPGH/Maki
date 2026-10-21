import { HOME_HERO_DEFAULTS, HOME_SECTIONS } from '../../../src/api/hooks'
import type {
  HomeReadingItem,
  HomeRecentSeriesItem,
  HomeSection,
  ProgressSummary,
  RecommendationItem,
  RecommendationsResult,
  UiSettings,
} from '../../../src/api/hooks'
import type { CustomRail, CustomRailItems } from '../../../src/api/customRails'
import type { HomeAnimeResumeItem, HomeAnimeResumePage } from '../../../src/api/animeResume'
import type { QueueHistoryDto, QueueItemDto, SeriesDto } from '../../../src/api/types'
import { cover, daysAgo, hoursAgo, minutesAgo } from '../api'
import { catalogue, uiSettings, type CatalogueEntry } from '../shared'

const OBTAINED_OVERRIDES: Record<number, number> = { 3: 60, 6: 180 }
const QUEUED: Record<number, { queued: number; downloading: number }> = {
  3: { queued: 4, downloading: 2 },
  9: { queued: 0, downloading: 1 },
}

function toSeries(entry: CatalogueEntry): SeriesDto {
  const have = OBTAINED_OVERRIDES[entry.id] ?? entry.chapters
  const queue = QUEUED[entry.id] ?? { queued: 0, downloading: 0 }
  return {
    id: entry.id,
    title: entry.title,
    displayTitle: entry.title,
    sortTitle: entry.title.toLowerCase(),
    originalTitle: entry.nativeTitle,
    altTitles: [],
    status: entry.status,
    type: entry.type.toLowerCase(),
    overview: entry.synopsis,
    year: entry.year,
    genres: entry.genres,
    metadataTags: entry.tags,
    contentRating: 'safe',
    tagIds: [],
    monitored: entry.id !== 11,
    monitorNewItems: 'All',
    rootFolderId: 1,
    folderName: entry.title,
    rootFolderPath: `/library/${entry.title}`,
    coverUrl: cover(entry.id),
    totalChapters: entry.chapters,
    totalVolumes: null,
    authorStory: entry.author,
    authorArt: entry.artist,
    publisher: null,
    rating: null,
    mangaBakaId: entry.mangaBakaId,
    aniListId: null,
    malId: null,
    kitsuId: null,
    links: [],
    numberingClash: null,
    added: daysAgo(30 + entry.id * 9),
    wantedChapterCount: entry.chapters,
    chapterFileCount: have,
    removedChapterCount: 0,
    readFileCleanup: 'Default',
    readFileCleanupActive: false,
    knownChapterCount: entry.chapters,
    queuedCount: queue.queued,
    downloadingCount: queue.downloading,
    hasAnime: entry.id === 7,
    animeName: entry.id === 7 ? 'Clockwork Tea House: The Animation' : null,
    animeStart: null,
    animeEnd: null,
    readChapterCount: entry.id === 8 ? null : Math.min(entry.read, have),
    sourceMatchPending: false,
    importLinkPending: false,
    incognito: 'Off',
    notificationMode: 'Default',
    sources: [entry.source],
    enabledSources: [entry.source],
    fileSources: [entry.source],
    lastReadAt: entry.read > 0 ? daysAgo(entry.id) : null,
    lastDownloadedAt: hoursAgo(entry.id * 5),
    communityRating: entry.rating,
    upgradeProfileId: null,
    lastUpgradeScan: null,
    pendingProposalId: null,
    lockedFields: [],
  } satisfies SeriesDto
}

export const librarySeries: SeriesDto[] = catalogue.map(toSeries)

const ACCENTS: Record<number, string | null> = {
  1: '#d98a3d',
  6: '#7a5bd1',
  3: '#3f8f9b',
  9: '#b8453f',
  7: '#4f9a62',
  11: '#c24f9a',
  4: null,
}

function reading(
  seriesId: number,
  chapter: number,
  page: number,
  pageCount: number,
  unread: number,
  lastRead: string,
  overrides: Partial<HomeReadingItem> = {},
): HomeReadingItem {
  const entry = catalogue.find((s) => s.id === seriesId)!
  return {
    seriesId,
    seriesTitle: entry.title,
    coverUrl: cover(seriesId),
    chapterId: seriesId * 1000 + chapter,
    chapterLabel: `Chapter ${chapter}`,
    page,
    pageCount,
    lastReadAt: lastRead,
    unreadChapters: unread,
    coverAccent: ACCENTS[seriesId] ?? null,
    ...overrides,
  }
}

export const continueReading: HomeReadingItem[] = [
  reading(1, 132, 11, 24, 11, minutesAgo(38)),
  reading(6, 151, 3, 19, 53, hoursAgo(6)),
  // A Kavita import: no page count, so no progress bar.
  reading(3, 41, 0, 0, 20, daysAgo(1)),
  reading(9, 19, 14, 22, 4, daysAgo(2)),
  reading(11, 21, 7, 18, 37, daysAgo(4)),
]

export const jumpBackIn: HomeReadingItem[] = [
  reading(7, 13, 0, 21, 107, daysAgo(9)),
  reading(4, 62, 0, 26, 2, daysAgo(15), { coverAccent: null }),
  reading(2, 97, 0, 20, 1, daysAgo(21), { coverUrl: null }),
]

function recent(
  seriesId: number,
  hours: number,
  newCount: number,
  label: string | null,
  readChapterId: number | null,
): HomeRecentSeriesItem {
  const entry = catalogue.find((s) => s.id === seriesId)!
  return {
    seriesId,
    seriesTitle: entry.title,
    coverUrl: cover(seriesId),
    addedAt: hoursAgo(hours),
    newChapterCount: newCount,
    newestChapterLabel: label,
    readChapterId,
  }
}

export const recentlyAdded: HomeRecentSeriesItem[] = [
  recent(5, 2, 1, 'Chapter 34', 5034),
  recent(9, 5, 2, 'Chapter 22', 9021),
  recent(1, 9, 3, 'Chapter 142', 1132),
  recent(3, 20, 12, 'Chapter 88', 3041),
  recent(10, 52, 1, 'Chapter 48', null),
  recent(12, 130, 4, null, 12076),
  recent(11, 200, 6, 'Chapter 57', 11021),
]

const PICK_TITLES = [
  'Hollow Orchard Postman',
  'Starlight Boarding House',
  'The Salt Cartographer',
  'Midnight Ramen Brigade',
  'Crimson Tide Academy',
  'Whispering Library of Ou',
  'Last Bus to Kiyomizu',
  'The Dragon Tailor Apprentice',
  'Pale Bloom Protocol',
  'Orbit Cafe Closing Time',
  'Seven Bells of Harrowfield',
  'A Fox in the Weather Office',
  'Tidewater Samurai Club',
  'Glass Harbor Nocturne',
]

const PICK_GENRES = [
  ['Drama', 'Romance'],
  ['Slice of Life', 'Comedy'],
  ['Adventure', 'Fantasy'],
  ['Comedy', 'Slice of Life'],
  ['Action', 'School Life'],
  ['Mystery', 'Fantasy'],
  ['Romance', 'Slice of Life'],
  ['Fantasy', 'Adventure'],
  ['Sci-Fi', 'Action'],
  ['Slice of Life', 'Romance'],
  ['Horror', 'Mystery'],
  ['Comedy', 'Supernatural'],
  ['Sports', 'Historical'],
  ['Drama', 'Mystery'],
]

export function catalogueItem(n: number, overrides: Partial<RecommendationItem> = {}): RecommendationItem {
  const title = PICK_TITLES[n % PICK_TITLES.length]
  const genres = PICK_GENRES[n % PICK_GENRES.length]
  const art = cover(title)
  return {
    providerId: String(20000 + n),
    title,
    coverUrl: art,
    thumbUrl: art,
    thumbUrlHiDpi: art,
    year: 2015 + (n % 10),
    description: 'An invented catalogue entry used by the Home stories.',
    status: n % 4 === 0 ? 'Completed' : 'Ongoing',
    rating: 68 + ((n * 7) % 25),
    totalChapters: 30 + n * 11,
    matchedGenres: genres,
    matchedTags: n % 3 === 0 ? ['Found Family', 'Travel'] : n % 3 === 1 ? ['Workplace'] : [],
    authorMatch: n % 5 === 0,
    relationKind: null,
    relatedToTitle: null,
    becauseOfTitle: n % 2 === 0 ? catalogue[n % 6].title : null,
    ...overrides,
  }
}

export const popularPicks: RecommendationItem[] = [
  ...PICK_TITLES.map((_, n) => catalogueItem(n)),
  // Already owned: the providerId matches a library series' mangaBakaId.
  catalogueItem(20, {
    providerId: String(catalogue[2].mangaBakaId),
    title: catalogue[2].title,
    thumbUrl: cover(3),
    thumbUrlHiDpi: cover(3),
    coverUrl: cover(3),
  }),
]

export const recommendedPicks: RecommendationItem[] = [
  catalogueItem(1, { relationKind: 'Sequel', relatedToTitle: catalogue[4].title, relatedToId: String(catalogue[4].mangaBakaId), becauseOfTitle: null }),
  catalogueItem(2, { authorMatch: true, becauseOfTitle: null }),
  catalogueItem(3, { coRead: true, coReadWithTitle: catalogue[1].title, coReadWithId: String(catalogue[1].mangaBakaId), becauseOfTitle: null, authorMatch: false }),
  catalogueItem(4),
  catalogueItem(5, { tasteMatch: true, becauseOfTitle: null, authorMatch: false }),
  catalogueItem(6),
  catalogueItem(7),
  catalogueItem(8),
  catalogueItem(9),
  catalogueItem(10),
]

export const recommendationsResult: RecommendationsResult = {
  related: [],
  similar: recommendedPicks,
  generatedAt: hoursAgo(3),
  page: 0,
  hasMore: false,
  poolVersion: 'story-pool',
}

function animeLibrary(seriesId: number, coveredTo: number, animeTitle: string): HomeAnimeResumeItem {
  const entry = catalogue.find((s) => s.id === seriesId)!
  return {
    seriesId,
    seriesTitle: entry.title,
    coverUrl: cover(seriesId),
    animeTitle,
    score: 8.1,
    coveredTo,
    coveredLabel: `Ch. ${coveredTo}`,
    resumeChapterId: seriesId * 1000 + coveredTo + 1,
    resumeChapterLabel: `Ch. ${coveredTo + 1}`,
    catalogue: null,
  }
}

function animeCatalogue(n: number, coveredTo: number): HomeAnimeResumeItem {
  const item = catalogueItem(n)
  return {
    seriesId: null,
    seriesTitle: item.title,
    coverUrl: item.thumbUrl,
    animeTitle: `${item.title} (TV)`,
    score: 7.6,
    coveredTo,
    coveredLabel: `Ch. ${coveredTo}`,
    resumeChapterId: null,
    resumeChapterLabel: null,
    catalogue: item,
  }
}

export const animeResumeItems: HomeAnimeResumeItem[] = [
  animeLibrary(7, 24, 'Clockwork Tea House: The Animation'),
  animeLibrary(11, 36, 'Neon Shrine Maiden Season 1'),
  animeLibrary(1, 60, 'Lanterns Over Kasumi'),
  animeCatalogue(2, 18),
  animeCatalogue(6, 42),
]

export const animeResumeAll: HomeAnimeResumeItem[] = [
  ...animeResumeItems,
  animeLibrary(6, 88, 'Ashen Crown Regression Part 1'),
  animeCatalogue(8, 12),
  animeCatalogue(9, 30),
  animeCatalogue(10, 54),
]

export const animeResumePage: HomeAnimeResumePage = { items: animeResumeItems, total: animeResumeAll.length }
export const animeResumeAllPage: HomeAnimeResumePage = { items: animeResumeAll, total: animeResumeAll.length }

let queueId = 100
export function queueItem(
  seriesId: number,
  chapter: number,
  status: string,
  overrides: Partial<QueueItemDto> = {},
): QueueItemDto {
  const entry = catalogue.find((s) => s.id === seriesId)!
  queueId += 1
  return {
    id: queueId,
    chapterId: seriesId * 1000 + chapter,
    seriesId,
    seriesTitle: entry.title,
    releaseTitle: null,
    chapterTitle: null,
    chapterVolume: null,
    chapterNumber: String(chapter),
    sourceName: entry.source,
    status,
    pagesTotal: 24,
    pagesDone: 0,
    retryCount: 0,
    nextAttempt: null,
    errorKey: null,
    errorParams: null,
    errorMessage: null,
    queuedAt: minutesAgo(12),
    completedAt: null,
    origin: 'monitorrefresh',
    upgrade: null,
    seriesCoverUrl: `/api/v1/mediacover/${seriesId}/cover.jpg`,
    ...overrides,
  }
}

export const downloadingItems: QueueItemDto[] = [
  queueItem(3, 61, 'Downloading', { pagesTotal: 28, pagesDone: 17 }),
  queueItem(3, 62, 'Downloading', { pagesTotal: 31, pagesDone: 4 }),
  queueItem(9, 23, 'FetchingPages'),
  queueItem(6, 204, 'Queued'),
  queueItem(6, 205, 'Queued'),
  queueItem(1, 143, 'Importing', { pagesTotal: 24, pagesDone: 24 }),
  queueItem(11, 58, 'RateLimited', { nextAttempt: minutesAgo(-4) }),
  queueItem(8, 1, 'Queued', {
    releaseTitle: '[Group] Bluefin Boys Complete Collection v01-v32 [Digital] [1080p] [Dual Audio] [Batch]',
    chapterNumber: null,
  }),
]

export const failedItem = queueItem(12, 76, 'Failed', {
  errorKey: 'error.download.noMoreSources',
  completedAt: minutesAgo(90),
})

export const queuePage = (items: QueueItemDto[]): QueueHistoryDto => ({
  items,
  total: items.length,
  page: 1,
  pageSize: 200,
})

export const progressSummary: ProgressSummary = {
  enabled: true,
  showStreaks: true,
  level: { level: 12, xp: 4820, intoLevel: 320, levelSpan: 700, nextLevelXp: 5200, progress: 320 / 700 },
  chaptersRead: 1284,
  readingSeconds: 213_000,
  seriesFinished: 9,
  daysRead: 118,
  currentStreak: 6,
  longestStreak: 31,
  earned: 14,
  total: 38,
  recent: [],
  goals: [{ id: 1, period: 'Week', metric: 'Chapters', target: 20, progress: 14 }],
}

export const customRails: CustomRail[] = [
  {
    id: 1,
    name: 'Unread favourites',
    placement: 'home',
    spec: { source: 'library', sort: 'read' },
    sortOrder: 0,
  },
  {
    id: 2,
    name: 'Cozy picks',
    placement: 'home',
    spec: {
      source: 'recommendations',
      filters: { genres: ['Slice of Life'] },
      seeds: [{ id: catalogue[1].mangaBakaId, title: catalogue[1].title }],
      obscurity: 0.2,
      diversity: 0.4,
    },
    sortOrder: 1,
  },
  {
    id: 3,
    name: 'Top rated romance',
    placement: 'home',
    spec: {
      source: 'catalogue',
      filters: { genres: ['Romance'] },
      sort: 'rating',
      excludeOwned: true,
    },
    sortOrder: 2,
  },
]

export function railItems(rail: CustomRail, limit: number): CustomRailItems {
  switch (rail.spec.source) {
    case 'library':
      return { source: 'library', items: [], seriesIds: [5, 9, 1, 3, 10, 12].slice(0, limit) }
    case 'recommendations':
      return { source: 'recommendations', items: recommendedPicks.slice(0, limit), seriesIds: [] }
    default:
      return { source: 'catalogue', items: popularPicks.slice(0, limit), seriesIds: [] }
  }
}

export interface HomeUiOptions {
  /** Section keys to switch off. */
  off?: string[]
  /** Custom rail ids to add to the layout, appended in this order. */
  rails?: number[]
  /** Overrides for the hero switch of the reading sections. */
  hero?: { continue?: boolean; jumpback?: boolean }
  /** A different section order; unlisted keys keep their shipping order after these. */
  order?: string[]
}

export function homeUi(options: HomeUiOptions = {}): UiSettings {
  const { off = [], rails = [], hero = {}, order = [] } = options
  const keys = [...order, ...HOME_SECTIONS.filter((k) => !order.includes(k))]
  const sections = [
    ...keys.map((key) => ({
      key,
      enabled: !off.includes(key),
      hero: key in HOME_HERO_DEFAULTS ? (hero[key as 'continue' | 'jumpback'] ?? HOME_HERO_DEFAULTS[key]) : null,
      panels: null,
    })),
    ...rails.map((id) => ({ key: `rail:${id}`, enabled: true, hero: null, panels: null })),
  ] as HomeSection[]
  return { ...uiSettings, homeLayout: { enabled: true, sections } }
}
