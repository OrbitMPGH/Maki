import type { SourceInfo, TagOption } from '../../../src/api/hooks'
import type { UpgradeProfileDto } from '../../../src/api/upgrades'
import type { LibraryFilterSpec, RootFolder, SavedFilterDto, SeriesDto, TagDto } from '../../../src/api/types'
import { cover, daysAgo, hoursAgo } from '../api'
import { catalogue, type CatalogueEntry } from '../shared'

const sourceKey: Record<string, string> = {
  MangaDex: 'mangadex',
  'Asura Scans': 'asura',
  Comick: 'comick',
  'Weeb Central': 'weebcentral',
}

export const rootFolders: RootFolder[] = [
  { id: 1, path: '/library/manga', freeSpace: 842_000_000_000, accessible: true },
  { id: 2, path: '/library/manhwa', freeSpace: 311_000_000_000, accessible: true },
  { id: 3, path: '/mnt/archive/old-shelf', freeSpace: null, accessible: false },
]

const tagDefs = [
  { id: 1, label: 'Favourites', color: 'red' },
  { id: 2, label: 'To re-read', color: 'blue' },
  { id: 3, label: 'Comfort reads', color: 'teal' },
  { id: 4, label: 'Waiting for volume', color: 'orange' },
  { id: 5, label: 'Shared with Sam', color: 'grape' },
  { id: 6, label: 'Unused label', color: 'gray' },
]

const tagIdsBySeries: Record<number, number[]> = {
  1: [1, 3],
  2: [3],
  3: [],
  4: [2, 4],
  5: [1, 3, 5],
  6: [],
  7: [4],
  8: [],
  9: [5],
  10: [1, 2],
  11: [],
  12: [],
}

interface Overrides {
  files: number
  wanted?: number
  monitored?: boolean
  contentRating?: string | null
  rating?: number | null
  queued?: number
  downloading?: number
  sources?: string[]
  enabledSources?: string[]
  profile?: number | null
  muted?: boolean
  lastRead?: number | null
  added: number
}

const overrides: Record<number, Overrides> = {
  1: { files: 140, monitored: true, contentRating: 'safe', rating: 9, queued: 2, lastRead: 1, added: 420 },
  2: { files: 96, monitored: false, contentRating: 'safe', rating: 8, lastRead: 40, added: 700 },
  3: { files: 70, wanted: 88, monitored: true, contentRating: 'suggestive', rating: null, downloading: 3, lastRead: 6, added: 220, profile: 1 },
  4: { files: 61, monitored: false, contentRating: 'safe', rating: 8, lastRead: 130, added: 900 },
  5: { files: 34, monitored: true, contentRating: 'safe', rating: 10, lastRead: 0, added: 150 },
  6: { files: 160, wanted: 203, monitored: true, contentRating: 'erotica', rating: 6, lastRead: 3, added: 380, muted: true },
  7: { files: 120, monitored: false, contentRating: 'safe', rating: null, lastRead: 200, added: 560 },
  8: { files: 210, monitored: false, contentRating: 'safe', rating: null, lastRead: null, added: 80, sources: [], enabledSources: [] },
  9: { files: 18, wanted: 22, monitored: true, contentRating: 'pornographic', rating: 7, lastRead: 2, added: 30, enabledSources: [] },
  10: { files: 48, monitored: false, contentRating: 'safe', rating: 10, lastRead: 90, added: 1200 },
  11: { files: 40, wanted: 57, monitored: true, contentRating: 'suggestive', rating: 6, lastRead: 12, added: 14, profile: 2 },
  12: { files: 75, monitored: false, contentRating: 'safe', rating: 5, lastRead: 300, added: 1500 },
  13: { files: 9, added: 0 },
  14: { files: 31, added: 9 },
}

function toSeries(e: CatalogueEntry): SeriesDto {
  const o = overrides[e.id]
  const key = sourceKey[e.source]
  const sources = o.sources ?? [key, ...(e.id % 3 === 0 ? ['comick'] : [])].filter((s, i, all) => all.indexOf(s) === i)
  return {
    id: e.id,
    title: e.title,
    displayTitle: e.title,
    sortTitle: e.title.toLowerCase(),
    originalTitle: e.nativeTitle || null,
    altTitles: e.nativeTitle ? [{ title: e.nativeTitle, language: 'ja' }] : [],
    status: e.status,
    type: e.type.toLowerCase(),
    overview: e.synopsis,
    year: e.year,
    genres: e.genres,
    metadataTags: e.tags,
    contentRating: o.contentRating ?? 'safe',
    tagIds: tagIdsBySeries[e.id] ?? [],
    monitored: o.monitored ?? true,
    monitorNewItems: 'All',
    archivedAt: null,
    archivedByUserId: null,
    rootFolderId: e.type === 'Manga' ? 1 : 2,
    folderName: e.title,
    rootFolderPath: `/library/${e.type === 'Manga' ? 'manga' : 'manhwa'}/${e.title}`,
    coverUrl: cover(e.id),
    totalChapters: e.chapters,
    totalVolumes: null,
    authorStory: e.author,
    authorArt: e.artist,
    publisher: null,
    rating: o.rating ?? null,
    mangaBakaId: e.mangaBakaId,
    aniListId: null,
    malId: null,
    kitsuId: null,
    links: [],
    numberingClash: null,
    added: daysAgo(o.added),
    wantedChapterCount: o.wanted ?? e.chapters,
    chapterFileCount: o.files,
    removedChapterCount: 0,
    readFileCleanup: 'Default',
    readFileCleanupActive: false,
    knownChapterCount: e.chapters,
    queuedCount: o.queued ?? 0,
    downloadingCount: o.downloading ?? 0,
    hasAnime: false,
    animeName: null,
    animeStart: null,
    animeEnd: null,
    readChapterCount: Math.min(e.read, o.files),
    sourceMatchPending: false,
    importLinkPending: false,
    incognito: 'Off',
    notificationMode: o.muted ? 'Muted' : 'Default',
    sources,
    enabledSources: o.enabledSources ?? sources,
    fileSources: sources.slice(0, 1),
    lastReadAt: o.lastRead == null ? null : daysAgo(o.lastRead),
    lastDownloadedAt: o.files > 0 ? hoursAgo(e.id * 7) : null,
    communityRating: Math.round(e.rating * 10) / 10,
    upgradeProfileId: o.profile ?? null,
    lastUpgradeScan: null,
    pendingProposalId: null,
    lockedFields: [],
  } satisfies SeriesDto
}

/** Fresh from an import: no metadata refresh yet, so type, rating and cover are all missing. */
const unrefreshed: SeriesDto = {
  ...toSeries({ ...catalogue[0], id: 13, title: 'untitled_import_0042', nativeTitle: '', mangaBakaId: 0 }),
  type: null,
  contentRating: null,
  overview: null,
  year: null,
  genres: [],
  metadataTags: [],
  mangaBakaId: null,
  coverUrl: null,
  totalChapters: null,
  wantedChapterCount: 0,
  knownChapterCount: 9,
  readChapterCount: null,
  monitored: false,
  sources: [],
  enabledSources: [],
  fileSources: ['import'],
  communityRating: null,
  lastReadAt: null,
  tagIds: [],
}

const longTitle: SeriesDto = {
  ...toSeries({
    ...catalogue[5],
    id: 14,
    title: 'I Was Reincarnated as the Villainess Duchess Who Quietly Runs the Entire Northern Border Trade Guild',
    nativeTitle: '',
    status: 'Ongoing',
    chapters: 31,
    read: 4,
    mangaBakaId: 10014,
  }),
  tagIds: [4],
}

export const librarySeries: SeriesDto[] = [...catalogue.map(toSeries), unrefreshed, longTitle]

export const libraryTags: TagDto[] = tagDefs.map((t) => ({
  ...t,
  seriesCount: librarySeries.filter((s) => s.tagIds.includes(t.id)).length,
}))

export const sources: SourceInfo[] = [
  { name: 'mangadex', displayName: 'MangaDex', baseUrl: 'https://mangadex.example' },
  { name: 'asura', displayName: 'Asura Scans', baseUrl: 'https://asura.example' },
  { name: 'comick', displayName: 'Comick', baseUrl: 'https://comick.example' },
  { name: 'weebcentral', displayName: 'Weeb Central', baseUrl: 'https://weebcentral.example' },
].map((s) => ({
  ...s,
  needsFlareSolverr: false,
  supportsLanguageFilter: s.name === 'mangadex',
  supportedLanguages: ['en'],
  enabled: true,
}))

export const upgradeProfiles: UpgradeProfileDto[] = [
  {
    id: 1,
    name: 'Official first',
    description: 'Prefers official and volume releases, upgrades until the cutoff tier is met.',
    tiers: [
      { tier: 'volume', allowed: true, grouped: false },
      { tier: 'official', allowed: true, grouped: false },
      { tier: 'scanlator', allowed: true, grouped: false },
      { tier: 'aggregator', allowed: true, grouped: false },
      { tier: 'unknown', allowed: false, grouped: false },
    ],
    cutoff: 'official',
    upgradesEnabled: true,
    minScoreDelta: 5,
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
  {
    id: 2,
    name: 'Whatever is fastest',
    description: null,
    tiers: [
      { tier: 'aggregator', allowed: true, grouped: true },
      { tier: 'scanlator', allowed: true, grouped: true },
      { tier: 'official', allowed: true, grouped: true },
      { tier: 'volume', allowed: true, grouped: false },
      { tier: 'unknown', allowed: true, grouped: false },
    ],
    cutoff: 'aggregator',
    upgradesEnabled: false,
    minScoreDelta: 0,
    maxTierScoreDrop: null,
    upgradeUntilScore: 0,
    formatScores: [],
    resolutionWeight: 0,
    compressionWeight: 0,
    pageTolerancePercent: 10,
    allowReplacingUnknown: false,
    version: 1,
    seriesCount: 1,
  },
]

const baseSpec: LibraryFilterSpec = {
  status: 'all',
  tagMatch: 'any',
  monitored: 'all',
  completeness: 'all',
  sort: 'added',
  genreMatch: 'any',
  metadataTagMatch: 'any',
  readMin: 0,
  readMax: 100,
  chapterMode: 'downloaded',
  sourceMatch: 'any',
  sourceState: 'all',
  fileSourceMatch: 'any',
}

export const savedFilters: SavedFilterDto[] = [
  { id: 1, name: 'Ongoing and behind', sortOrder: 0, spec: { ...baseSpec, status: 'Ongoing', completeness: 'behind', sort: 'incomplete' } },
  { id: 2, name: 'Favourites', sortOrder: 1, spec: { ...baseSpec, tagIds: [1], sort: 'title' } },
  { id: 3, name: 'Finished, never rated', sortOrder: 2, spec: { ...baseSpec, status: 'Completed', ratingState: 'unrated' } },
]

/** A slice of the recommendation tag vocabulary: enough to browse into a group, search and tick. */
export const recommendationTags: TagOption[] = [
  { name: 'Character Traits', path: '', count: 0, hasSubtags: true },
  { name: 'Stoic', path: 'Character Traits', count: 1840, hasSubtags: false },
  { name: 'Clumsy', path: 'Character Traits', count: 960, hasSubtags: false },
  { name: 'Genius', path: 'Character Traits', count: 2210, hasSubtags: true },
  { name: 'Child Prodigy', path: 'Character Traits > Genius', count: 310, hasSubtags: false },
  { name: 'Locations', path: '', count: 0, hasSubtags: true },
  { name: 'School', path: 'Locations', count: 5400, hasSubtags: true },
  { name: 'College', path: 'Locations > School', count: 820, hasSubtags: false },
  { name: 'Island', path: 'Locations', count: 410, hasSubtags: false },
  { name: 'Cooking', path: '', count: 1330, hasSubtags: false },
  { name: 'Time Loop', path: '', count: 540, hasSubtags: false },
  { name: 'Found Family', path: '', count: 1975, hasSubtags: false },
]
