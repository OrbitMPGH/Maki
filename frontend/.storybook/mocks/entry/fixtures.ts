import type { ReaderManifest, ReaderSettings } from '../../../src/api/reader'
import type { ReadingProfile } from '../../../src/api/readingProfiles'
import { DEFAULT_PREFS, type ReaderPrefs } from '../../../src/pages/reader/prefs'
import type {
  MangaBakaDetail,
  RecommendationItem,
  LibrarySettings,
} from '../../../src/api/hooks'
import type { SeriesDto } from '../../../src/api/types'
import type { UpgradeProfileDto } from '../../../src/api/upgrades'
import type { SeriesRequest } from '../../../src/api/requests'
import { cover, daysAgo } from '../api'
import { catalogue, seriesById, type CatalogueEntry } from '../shared'

/** The page URL the real manifest flow produces; the shared handler answers it with a generated page. */
export const pageUrlFor = (chapterId: number, index: number, thumb = false) =>
  `/api/v1/reader/chapter/${chapterId}/${thumb ? 'thumb' : 'page'}/${index}`

export const pageUrlsFor = (chapterId: number, count: number, thumb = false) =>
  Array.from({ length: count }, (_, i) => pageUrlFor(chapterId, i, thumb))

export const mangaPrefs: ReaderPrefs = { ...DEFAULT_PREFS, mode: 'paged', direction: 'rtl', fit: 'height' }
export const webtoonPrefs: ReaderPrefs = { ...DEFAULT_PREFS, mode: 'vertical', direction: 'ltr', fit: 'width', pageGap: 0 }
export const doublePrefs: ReaderPrefs = { ...DEFAULT_PREFS, mode: 'double', direction: 'rtl', fit: 'height' }

export const readingProfiles: ReadingProfile[] = [
  { id: 1, name: 'Manga', prefs: mangaPrefs, seriesTypes: ['manga'] },
  { id: 2, name: 'Webtoon', prefs: webtoonPrefs, seriesTypes: ['manhwa', 'manhua'] },
  { id: 3, name: 'Night reading', prefs: { ...mangaPrefs, dim: 30, filter: 'sepia' }, seriesTypes: [] },
]

export const readerSettings: ReaderSettings = {
  defaults: mangaPrefs,
  pushToKavita: false,
  kavitaUserId: null,
  pullFromKavita: false,
  kavitaLive: 'Off',
}

/** Build a manifest for any series in the shared catalogue; chapter ids are invented. */
export function makeManifest(overrides: Partial<ReaderManifest> & Pick<ReaderManifest, 'chapterId' | 'seriesId'>): ReaderManifest {
  const series = seriesById(overrides.seriesId)
  return {
    seriesTitle: series.title,
    label: 'Chapter 1',
    number: 1,
    pageCount: 18,
    seriesChapterCount: series.chapters,
    seriesReadCount: series.read,
    seriesWantedCount: series.chapters,
    resumePage: 0,
    completed: false,
    previousChapterId: null,
    nextChapterId: null,
    nextChapterLabel: null,
    nextChapterNumber: null,
    seriesCoverUrl: cover(series.id),
    prefs: mangaPrefs,
    prefsSource: 'Profile',
    profileId: 1,
    pinnedProfileId: null,
    autoProfileId: 1,
    pageVersion: 'v1',
    ...overrides,
  }
}

/**
 * Chapters the reader stories open, by id. Anything else answers 404 ("no readable file"); 500 and
 * 503 are reserved for the error stories, which override the endpoint instead.
 */
export const chapters: Record<number, ReaderManifest> = {
  // Mid-series manga, resumes on page 6, a previous and a next chapter.
  1132: makeManifest({
    chapterId: 1132,
    seriesId: 1,
    label: 'Chapter 132',
    number: 132,
    pageCount: 18,
    resumePage: 5,
    previousChapterId: 1131,
    nextChapterId: 1133,
    nextChapterLabel: 'Chapter 133',
    nextChapterNumber: 133,
    seriesChapterCount: 142,
    seriesReadCount: 131,
    seriesWantedCount: 150,
  }),
  // The newest chapter on disk, with more wanted: the end screen has no next chapter to offer.
  1142: makeManifest({
    chapterId: 1142,
    seriesId: 1,
    label: 'Chapter 142',
    number: 142,
    pageCount: 12,
    resumePage: 11,
    previousChapterId: 1141,
    seriesChapterCount: 142,
    seriesReadCount: 141,
    seriesWantedCount: 150,
  }),
  // A finished series: the last chapter, everything read.
  2096: makeManifest({
    chapterId: 2096,
    seriesId: 2,
    label: 'Chapter 96: The ninth bowl',
    number: 96,
    pageCount: 24,
    resumePage: 23,
    previousChapterId: 2095,
    seriesChapterCount: 96,
    seriesReadCount: 95,
    seriesWantedCount: 96,
  }),
  // Webtoon in continuous mode.
  3040: makeManifest({
    chapterId: 3040,
    seriesId: 3,
    label: 'Chapter 40',
    number: 40,
    pageCount: 30,
    resumePage: 0,
    previousChapterId: 3039,
    nextChapterId: 3041,
    nextChapterLabel: 'Chapter 41',
    nextChapterNumber: 41,
    prefs: webtoonPrefs,
    profileId: 2,
    autoProfileId: 2,
  }),
  // Opens at a chapter the downloads skipped past: 19 jumps to 22.
  6019: makeManifest({
    chapterId: 6019,
    seriesId: 6,
    label: 'Chapter 19',
    number: 19,
    pageCount: 14,
    resumePage: 13,
    previousChapterId: 6018,
    nextChapterId: 6022,
    nextChapterLabel: 'Chapter 22',
    nextChapterNumber: 22,
    prefs: { ...mangaPrefs, autoNextChapter: false },
    prefsSource: 'Series',
    profileId: null,
  }),
  // Double-page layout.
  1201: makeManifest({
    chapterId: 1201,
    seriesId: 10,
    label: 'Chapter 1: The first letter',
    number: 1,
    pageCount: 20,
    resumePage: 0,
    nextChapterId: 1202,
    nextChapterLabel: 'Chapter 2',
    nextChapterNumber: 2,
    prefs: doublePrefs,
    prefsSource: 'Global',
    profileId: null,
    autoProfileId: null,
  }),
  // Every page of this one fails to load (the story swaps the page route for a 500).
  4404: makeManifest({
    chapterId: 4404,
    seriesId: 4,
    label: 'Chapter 61',
    number: 61,
    pageCount: 10,
    seriesChapterCount: 61,
    seriesReadCount: 60,
    seriesWantedCount: 61,
  }),
  // A one-shot: no number, one chapter in the whole series.
  9001: makeManifest({
    chapterId: 9001,
    seriesId: 4,
    label: 'Special: Midnight edition',
    number: null,
    pageCount: 8,
    resumePage: 0,
    seriesChapterCount: 1,
    seriesReadCount: 0,
    seriesWantedCount: 1,
  }),
}

// ---------------------------------------------------------------------------------------------
// Add series
// ---------------------------------------------------------------------------------------------

export const providerIdOf = (entry: CatalogueEntry) => String(entry.mangaBakaId)

export function catalogueItem(entry: CatalogueEntry): RecommendationItem {
  return {
    providerId: providerIdOf(entry),
    title: entry.title,
    coverUrl: cover(entry.id),
    thumbUrl: cover(entry.id),
    thumbUrlHiDpi: cover(entry.id),
    year: entry.year,
    description: entry.synopsis,
    status: entry.status,
    rating: entry.rating,
    totalChapters: entry.chapters,
    matchedGenres: [],
    matchedTags: [],
    authorMatch: false,
    relationKind: null,
    relatedToTitle: null,
    becauseOfTitle: null,
    coRecommended: false,
    coRead: false,
    tasteMatch: false,
    franchiseId: null,
  }
}

export const catalogueItems: RecommendationItem[] = catalogue.map(catalogueItem)

/** Catalogue ids already in the library, so some cards read "in library" and some can be added. */
export const OWNED_IDS = [1, 2, 5]

export function librarySeries(entry: CatalogueEntry): SeriesDto {
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
    monitored: true,
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
    rating: entry.rating,
    mangaBakaId: entry.mangaBakaId,
    aniListId: null,
    malId: null,
    kitsuId: null,
    links: [],
    numberingClash: null,
    added: daysAgo(90),
    wantedChapterCount: 0,
    chapterFileCount: entry.chapters,
    removedChapterCount: 0,
    readFileCleanup: 'Off',
    readFileCleanupActive: false,
    knownChapterCount: entry.chapters,
    queuedCount: 0,
    downloadingCount: 0,
    hasAnime: false,
    animeName: null,
    animeStart: null,
    animeEnd: null,
    readChapterCount: entry.read,
    sourceMatchPending: false,
    importLinkPending: false,
    incognito: 'Off',
    notificationMode: 'Default',
    upgradeProfileId: null,
    lastUpgradeScan: null,
    pendingProposalId: null,
    lockedFields: [],
  }
}

export const librarySeriesList: SeriesDto[] = catalogue.filter((s) => OWNED_IDS.includes(s.id)).map(librarySeries)

export function catalogueDetail(entry: CatalogueEntry): MangaBakaDetail {
  return {
    providerId: providerIdOf(entry),
    title: entry.title,
    nativeTitle: entry.nativeTitle,
    romanizedTitle: null,
    altTitles: [],
    description: entry.synopsis,
    coverUrl: cover(entry.id),
    year: entry.year,
    type: entry.type.toLowerCase(),
    status: entry.status,
    contentRating: 'safe',
    rating: entry.rating * 10,
    sourceRatings: [
      { source: 'MangaUpdates', rating: Math.round(entry.rating * 10) },
      { source: 'AniList', rating: Math.round(entry.rating * 10 - 3) },
    ],
    totalChapters: entry.chapters,
    finalVolume: null,
    authors: [entry.author],
    artists: entry.artist === entry.author ? [] : [entry.artist],
    publishers: [],
    genres: entry.genres,
    tags: entry.tags.map((name, i) => ({ name, weight: i === 0 ? 'Defining' : 'Supporting', description: null, isSpoiler: false })),
    links: [],
    malId: null,
    hasAnime: false,
    animeStart: null,
    animeEnd: null,
    readerHint: null,
    animeResume: null,
  }
}

export const rootFolders = [
  { id: 1, path: '/library/manga', freeSpace: 412_000_000_000, accessible: true },
  { id: 2, path: '/mnt/archive/webtoons', freeSpace: 1_800_000_000_000, accessible: true },
]

export const librarySettings: LibrarySettings = {
  writeComicInfo: true,
  folderNamingMode: 'rename',
  incognitoByRating: { safe: 'Off', suggestive: 'Off', erotica: 'ScrobbleOnly', pornographic: 'Full' },
  newSeriesMonitorMode: 'All',
}

export const upgradeProfiles: UpgradeProfileDto[] = [
  {
    id: 1,
    name: 'Official first',
    description: 'Prefers licensed releases, falls back to scanlators.',
    tiers: [
      { tier: 'official', allowed: true, grouped: false },
      { tier: 'scanlator', allowed: true, grouped: false },
      { tier: 'aggregator', allowed: false, grouped: false },
    ],
    cutoff: 'official',
    upgradesEnabled: true,
    minScoreDelta: 10,
    maxTierScoreDrop: null,
    upgradeUntilScore: 0,
    formatScores: [],
    resolutionWeight: 0,
    compressionWeight: 0,
    pageTolerancePercent: 10,
    allowReplacingUnknown: true,
    version: 1,
    seriesCount: 4,
  },
  {
    id: 2,
    name: 'Anything fast',
    description: null,
    tiers: [{ tier: 'aggregator', allowed: true, grouped: false }],
    cutoff: 'aggregator',
    upgradesEnabled: false,
    minScoreDelta: 0,
    maxTierScoreDrop: null,
    upgradeUntilScore: 0,
    formatScores: [],
    resolutionWeight: 0,
    compressionWeight: 0,
    pageTolerancePercent: 10,
    allowReplacingUnknown: true,
    version: 1,
    seriesCount: 1,
  },
]

export function seriesRequest(entry: CatalogueEntry, overrides: Partial<SeriesRequest> = {}): SeriesRequest {
  return {
    id: 31,
    kind: 'NewSeries',
    status: 'Pending',
    userId: 2,
    requestedBy: 'Rin',
    metadataProviderId: providerIdOf(entry),
    seriesId: null,
    title: entry.title,
    coverUrl: cover(entry.id),
    year: entry.year,
    chapterStart: null,
    chapterEnd: null,
    note: null,
    created: daysAgo(1),
    resolvedAt: null,
    resolvedBy: null,
    resolutionNote: null,
    queuedCount: null,
    editedAt: null,
    editedBy: null,
    originalChapterStart: null,
    originalChapterEnd: null,
    ...overrides,
  }
}
