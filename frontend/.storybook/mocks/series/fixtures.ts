import type {
  ChapterDto,
  CompareSnapshot,
  ChapterFileQualityDto,
  MetadataLink,
  SeriesDto,
  SeriesFileDto,
  SeriesScrobbleDto,
  SourceMappingDto,
  TagDto,
} from '../../../src/api/types'
import type {
  MangaBakaDetail,
  RecommendationItem,
  ReleaseRowDto,
  RelinkPlan,
  ResolvedSourceUrl,
  SeriesRenamePlan,
  SourceInfo,
  SourceSearchResult,
} from '../../../src/api/hooks'
import type { ChapterProgressDto } from '../../../src/api/reader'
import type { SeriesAnimeResume } from '../../../src/api/animeResume'
import type {
  SeriesScanStatus,
  SourceOrderDto,
  TorrentProposalDto,
  UpgradeProfileDto,
} from '../../../src/api/upgrades'
import { catalogue, seriesById, type CatalogueEntry } from '../shared'
import { cover, daysAgo, hoursAgo } from '../api'

const sourceKeys: Record<string, string> = {
  MangaDex: 'mangadex',
  'Asura Scans': 'asurascans',
  Comick: 'comick',
  'Weeb Central': 'weebcentral',
}
export const sourceKey = (entry: CatalogueEntry) => sourceKeys[entry.source] ?? 'mangadex'

export const tags: TagDto[] = [
  { id: 1, label: 'Favourites', color: 'grape', seriesCount: 6 },
  { id: 2, label: 'Reading now', color: 'blue', seriesCount: 4 },
  { id: 3, label: 'Weekend binge', color: 'orange', seriesCount: 3 },
  { id: 4, label: 'Re-read someday', color: 'teal', seriesCount: 2 },
]

export const rootFolders = [
  { id: 1, path: '/library/manga', freeSpace: 412_000_000_000, accessible: true },
  { id: 2, path: '/library/manhwa', freeSpace: 98_000_000_000, accessible: true },
]

export const upgradeProfiles: UpgradeProfileDto[] = [
  {
    id: 1,
    name: 'Balanced',
    description: 'Prefers official releases, accepts scanlators.',
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
    resolutionWeight: 100,
    compressionWeight: 50,
    pageTolerancePercent: 10,
    allowReplacingUnknown: true,
    version: 1,
    seriesCount: 7,
  },
  {
    id: 2,
    name: 'Archive quality',
    description: null,
    tiers: [
      { tier: 'volume', allowed: true, grouped: false },
      { tier: 'official', allowed: true, grouped: false },
      { tier: 'scanlator', allowed: false, grouped: false },
      { tier: 'aggregator', allowed: false, grouped: false },
      { tier: 'unknown', allowed: false, grouped: false },
    ],
    cutoff: 'volume',
    upgradesEnabled: true,
    minScoreDelta: 10,
    maxTierScoreDrop: 20,
    upgradeUntilScore: 0,
    formatScores: [],
    resolutionWeight: 200,
    compressionWeight: 100,
    pageTolerancePercent: 5,
    allowReplacingUnknown: false,
    version: 1,
    seriesCount: 2,
  },
]

export const fileQuality = (fileId: number, overrides: Partial<ChapterFileQualityDto> = {}): ChapterFileQualityDto => ({
  fileId,
  tier: 'scanlator',
  group: 'Lantern Scans',
  pageCount: 24,
  medianWidth: 1200,
  medianHeight: 1800,
  imageFormat: 'jpg',
  measured: true,
  score: 62,
  cutoffMet: false,
  trusted: false,
  ...overrides,
})

export function links(entry: CatalogueEntry): MetadataLink[] {
  return [
    { site: 'mangabaka', url: `https://mangabaka.example/series/${entry.mangaBakaId}` },
    { site: 'anilist', url: `https://anilist.example/manga/${entry.mangaBakaId}` },
    { site: 'myanimelist', url: `https://myanimelist.example/manga/${entry.mangaBakaId}` },
  ]
}

export function seriesDto(id: number, overrides: Partial<SeriesDto> = {}): SeriesDto {
  const entry = seriesById(id)
  const downloaded = Math.min(entry.chapters, Math.max(entry.read, Math.round(entry.chapters * 0.92)))
  return {
    id: entry.id,
    title: entry.title,
    displayTitle: entry.title,
    sortTitle: entry.title.toLowerCase(),
    originalTitle: entry.nativeTitle,
    altTitles: [
      { title: entry.title, language: 'en' },
      { title: entry.nativeTitle, language: entry.type === 'Manhwa' ? 'ko' : entry.type === 'Manhua' ? 'zh' : 'ja' },
    ],
    status: entry.status,
    type: entry.type.toLowerCase(),
    overview: entry.synopsis,
    year: entry.year,
    genres: entry.genres,
    metadataTags: entry.tags,
    contentRating: 'safe',
    tagIds: entry.id % 2 === 1 ? [1, 2] : [3],
    monitored: entry.status === 'Ongoing',
    monitorNewItems: 'all',
    archivedAt: null,
    archivedByUserId: null,
    rootFolderId: entry.type === 'Manhwa' ? 2 : 1,
    folderName: entry.title,
    rootFolderPath: `/library/${entry.type === 'Manhwa' ? 'manhwa' : 'manga'}/${entry.title}`,
    coverUrl: cover(entry.id),
    totalChapters: entry.status === 'Ongoing' ? null : entry.chapters,
    totalVolumes: Math.ceil(entry.chapters / 9),
    authorStory: entry.author,
    authorArt: entry.artist,
    publisher: 'Northwind Press',
    rating: entry.id % 3 === 0 ? 9 : null,
    mangaBakaId: entry.mangaBakaId,
    aniListId: entry.mangaBakaId + 100,
    malId: entry.mangaBakaId + 200,
    kitsuId: null,
    links: links(entry),
    numberingClash: null,
    added: daysAgo(210),
    wantedChapterCount: entry.chapters,
    chapterFileCount: downloaded,
    removedChapterCount: 0,
    readFileCleanup: 'Default',
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
    readTimeEstimate:
      entry.read < entry.chapters
        ? {
            seconds: (entry.chapters - entry.read) * 540,
            remainingChapters: entry.chapters - entry.read,
            style: 'paged',
            sampleChapters: 12,
            seriesSpecific: true,
          }
        : null,
    upgradeProfileId: null,
    lastUpgradeScan: null,
    pendingProposalId: null,
    lockedFields: [],
    ...overrides,
  } satisfies SeriesDto
}

export interface ChapterShape {
  /** Chapter numbers wanted but with no file. */
  missing?: number[]
  /** Chapter numbers the user never asked for. */
  unwanted?: number[]
  /** Decimal specials to splice in, e.g. 10.5. */
  specials?: number[]
  /** The last N chapters have no file yet. Defaults to the gap between the catalogue's chapters and files. */
  trailingMissing?: number
  /** Leading chapters that carry a low-quality aggregator file. */
  upgradeable?: number
  /** Cap the chapter count so a story with a long series stays cheap to render. */
  limit?: number
}

export function chaptersFor(id: number, shape: ChapterShape = {}): ChapterDto[] {
  const entry = seriesById(id)
  const count = Math.min(entry.chapters, shape.limit ?? entry.chapters)
  const missing = new Set(shape.missing ?? [])
  const unwanted = new Set(shape.unwanted ?? [])
  const trailing = shape.trailingMissing ?? Math.max(0, count - Math.round(count * 0.92))
  const numbers: number[] = []
  for (let n = 1; n <= count; n++) numbers.push(n)
  for (const s of shape.specials ?? []) numbers.push(s)
  numbers.sort((a, b) => a - b)
  return numbers.map((n, index) => {
    const hasFile = !missing.has(n) && !unwanted.has(n) && n <= count - trailing
    const chapterId = id * 1000 + Math.round(n * 10)
    const upgradeable = n <= (shape.upgradeable ?? 0)
    return {
      id: chapterId,
      seriesId: id,
      number: n,
      numberRaw: String(n),
      volume: Math.ceil(Math.floor(n) / 9),
      title: n % 4 === 0 ? `The long way round` : n % 7 === 0 ? null : `Chapter ${n}`,
      isOneShot: false,
      language: 'en',
      releaseDate: daysAgo((numbers.length - index) * 7),
      wanted: !unwanted.has(n),
      hasFile,
      fileRemovedAt: null,
      fileAutoRemoved: false,
      fileDeleteDueAt: null,
      filePath: hasFile ? `/library/manga/${entry.title}/${entry.title} - Ch.${n}.cbz` : null,
      fileSourceName: hasFile ? sourceKey(entry) : null,
      fileReleaseName: null,
      fileVolume: null,
      fileQuality: hasFile
        ? upgradeable
          ? fileQuality(chapterId, { tier: 'aggregator', group: null, medianWidth: 800, score: 31, cutoffMet: false })
          : fileQuality(chapterId, { tier: 'official', group: null, score: 88, cutoffMet: true })
        : null,
    } satisfies ChapterDto
  })
}

export function progressFor(id: number, chapters: ChapterDto[], readOverride?: number): ChapterProgressDto[] {
  const read = readOverride ?? seriesById(id).read
  return chapters
    .filter((c) => c.number != null && c.number <= read && c.hasFile)
    .map((c, i) => ({
      chapterId: c.id,
      pageIndex: 23,
      pageCount: 24,
      completed: true,
      external: false,
      watched: false,
      unreadAt: null,
      updatedAt: hoursAgo(2 + i),
    }))
}

export const sourceInfos: SourceInfo[] = [
  {
    name: 'mangadex',
    displayName: 'MangaDex',
    baseUrl: 'https://mangadex.example',
    needsFlareSolverr: false,
    supportsLanguageFilter: true,
    supportedLanguages: ['en', 'es', 'fr'],
    enabled: true,
    kind: 'aggregator',
    content: ['manga'],
    rating: 'general',
    defaultEnabled: true,
    defaultRank: 1,
    quality: { rating: 'good', bitsPerPixel: 1.4, basis: 'library', samples: 48, series: 9 },
  },
  {
    name: 'asurascans',
    displayName: 'Asura Scans',
    baseUrl: 'https://asura.example',
    needsFlareSolverr: true,
    supportsLanguageFilter: false,
    supportedLanguages: ['en'],
    enabled: true,
    kind: 'scanlator',
    content: ['manhwa'],
    rating: 'general',
    defaultEnabled: true,
    defaultRank: 2,
    quality: { rating: 'fair', bitsPerPixel: 0.9, basis: 'baseline', samples: 10, series: 3 },
  },
  {
    name: 'comick',
    displayName: 'Comick',
    baseUrl: 'https://comick.example',
    needsFlareSolverr: false,
    supportsLanguageFilter: true,
    supportedLanguages: ['en', 'pt'],
    enabled: true,
    kind: 'aggregator',
    content: ['manga', 'manhwa', 'manhua'],
    rating: 'general',
    defaultEnabled: true,
    defaultRank: 3,
    quality: null,
  },
  {
    name: 'weebcentral',
    displayName: 'Weeb Central',
    baseUrl: 'https://weebcentral.example',
    needsFlareSolverr: false,
    supportsLanguageFilter: false,
    supportedLanguages: ['en'],
    enabled: true,
    kind: 'aggregator',
    content: ['manga'],
    rating: 'general',
    defaultEnabled: true,
    defaultRank: 4,
  },
  {
    name: 'shonenplaza',
    displayName: 'Shonen Plaza',
    baseUrl: 'https://plaza.example',
    needsFlareSolverr: false,
    supportsLanguageFilter: false,
    supportedLanguages: ['en'],
    enabled: false,
    kind: 'official',
    content: ['manga'],
    rating: 'general',
    defaultEnabled: false,
    defaultRank: 5,
  },
]

export function mappingsFor(id: number): SourceMappingDto[] {
  const entry = seriesById(id)
  const primary = sourceKey(entry)
  const rows: SourceMappingDto[] = [
    {
      id: id * 10 + 1,
      seriesId: id,
      sourceName: primary,
      sourceSeriesId: `${primary}-${entry.mangaBakaId}`,
      url: `https://${primary}.example/title/${entry.mangaBakaId}`,
      languageFilter: null,
      priority: 1,
      enabled: true,
      lastRefresh: hoursAgo(3),
      lastError: null,
      chapterSnapshotAt: hoursAgo(3),
      origin: 'CrossId',
    },
    {
      id: id * 10 + 2,
      seriesId: id,
      sourceName: 'comick',
      sourceSeriesId: `ck-${entry.mangaBakaId}`,
      url: `https://comick.example/comic/${entry.mangaBakaId}`,
      languageFilter: 'en,pt',
      priority: 2,
      enabled: true,
      lastRefresh: hoursAgo(5),
      lastError: 'Rate limited, retrying after the cooldown',
      chapterSnapshotAt: hoursAgo(30),
      origin: 'TitleSearch',
    },
    {
      id: id * 10 + 3,
      seriesId: id,
      sourceName: 'weebcentral',
      sourceSeriesId: `wc-${entry.mangaBakaId}`,
      url: `https://weebcentral.example/series/${entry.mangaBakaId}`,
      languageFilter: null,
      priority: 3,
      enabled: false,
      lastRefresh: daysAgo(12),
      lastError: null,
      chapterSnapshotAt: daysAgo(12),
      origin: 'Manual',
    },
  ]
  return rows.filter((r, i) => i === 0 || r.sourceName !== primary)
}

export function sourceOrderFor(id: number): SourceOrderDto {
  const rows = mappingsFor(id)
  const enabled = rows.filter((r) => r.enabled)
  return {
    seriesMode: null,
    defaultMode: 'manual',
    mode: 'manual',
    order: [...enabled, ...rows.filter((r) => !r.enabled)].map((r) => r.id),
    sources: rows.slice(0, 2).map((r, i) => ({
      mappingId: r.id,
      samples: 12 - i * 5,
      medianWidth: 1280 - i * 300,
      bitsPerPixel: 1.6 - i * 0.5,
      imageFormat: i === 0 ? 'webp' : 'jpg',
      latestUtc: hoursAgo(20),
      reliable: i === 0,
      resolutionPoints: null,
      compressionPoints: null,
    })),
    scout: null,
    qualityOrder: enabled.map((r) => r.id),
    tiers: Object.fromEntries(rows.map((r, i) => [r.id, i === 0 ? 'official' : 'aggregator'])) as SourceOrderDto['tiers'],
  }
}

export function filesFor(id: number, chapters: ChapterDto[]): SeriesFileDto[] {
  const title = seriesById(id).title
  const rows: SeriesFileDto[] = chapters
    .filter((c) => c.hasFile)
    .slice(0, 12)
    .map((c, i) => ({
      relativePath: `${title} - Ch.${c.number}.cbz`,
      fileName: `${title} - Ch.${c.number}.cbz`,
      size: 18_000_000 + i * 750_000,
      sourceName: c.fileSourceName,
      onDisk: true,
      status: 'linked',
      parsedLabel: `Ch.${c.number}`,
      isVolume: false,
      mappedChapters: [String(c.number)],
      quality: c.fileQuality,
    }))
  rows.push(
    {
      relativePath: 'extras/Bonus booklet.cbz',
      fileName: 'Bonus booklet.cbz',
      size: 31_000_000,
      sourceName: null,
      onDisk: true,
      status: 'unrecognized',
      parsedLabel: null,
      isVolume: false,
      mappedChapters: [],
      quality: null,
    },
    {
      relativePath: `${title} v03.cbz`,
      fileName: `${title} v03.cbz`,
      size: 212_000_000,
      sourceName: null,
      onDisk: true,
      status: 'unlinked',
      parsedLabel: 'Vol.3',
      isVolume: true,
      mappedChapters: [],
      quality: null,
    },
    {
      relativePath: `${title} - Ch.9999.cbz`,
      fileName: `${title} - Ch.9999.cbz`,
      size: 0,
      sourceName: 'mangadex',
      onDisk: false,
      status: 'missing',
      parsedLabel: 'Ch.9999',
      isVolume: false,
      mappedChapters: ['9999'],
      quality: null,
    },
  )
  return rows
}

export const scrobble: SeriesScrobbleDto = {
  configured: true,
  matched: true,
  kavitaSeriesId: 412,
  services: [
    {
      service: 'anilist',
      label: 'AniList',
      connected: true,
      remoteId: '104512',
      method: 'library',
      url: 'https://anilist.example/manga/104512',
      chapter: 131,
      volume: 14,
      status: 'reading',
      syncedAt: hoursAgo(6),
      error: null,
      reviewReason: null,
      reviewCandidates: [],
    },
    {
      service: 'myanimelist',
      label: 'MyAnimeList',
      connected: true,
      remoteId: null,
      method: null,
      url: null,
      chapter: 0,
      volume: 0,
      status: null,
      syncedAt: null,
      error: null,
      reviewReason: 'ambiguous',
      reviewCandidates: [
        { id: '5521', title: 'Lanterns Over Kasumi', url: 'https://myanimelist.example/manga/5521' },
        { id: '5522', title: 'Lanterns Over Kasumi: Side Stories', url: 'https://myanimelist.example/manga/5522' },
      ],
    },
    {
      service: 'mangaupdates',
      label: 'MangaUpdates',
      connected: false,
      remoteId: null,
      method: null,
      url: null,
      chapter: 0,
      volume: 0,
      status: null,
      syncedAt: null,
      error: null,
      reviewReason: null,
      reviewCandidates: [],
    },
  ],
}

export function recommendationItem(entry: CatalogueEntry, extra: Partial<RecommendationItem> = {}): RecommendationItem {
  return {
    providerId: String(entry.mangaBakaId),
    title: entry.title,
    coverUrl: cover(entry.id),
    thumbUrl: cover(entry.id),
    thumbUrlHiDpi: cover(entry.id),
    year: entry.year,
    description: entry.synopsis,
    status: entry.status.toLowerCase(),
    rating: entry.rating * 10,
    totalChapters: entry.chapters,
    matchedGenres: entry.genres.slice(0, 2),
    matchedTags: entry.tags.slice(0, 1),
    authorMatch: false,
    relationKind: null,
    relatedToTitle: null,
    becauseOfTitle: null,
    ...extra,
  }
}

export function relatedItems(id: number): RecommendationItem[] {
  const others = catalogue.filter((s) => s.id !== id)
  const from = seriesById(id).title
  return [
    recommendationItem(others[2], { relationKind: 'sequel', relatedToTitle: from }),
    recommendationItem(others[5], { relationKind: 'spinoff', relatedToTitle: from }),
    recommendationItem(others[8], { relationKind: 'side_story', relatedToTitle: from }),
  ]
}

export function similarItems(id: number): RecommendationItem[] {
  const from = seriesById(id).title
  return catalogue
    .filter((s) => s.id !== id)
    .slice(0, 8)
    .map((s, i) =>
      recommendationItem(s, {
        becauseOfTitle: from,
        authorMatch: i === 1,
        tasteMatch: i % 3 === 0,
        coRead: i === 2,
        coReadWithTitle: i === 2 ? from : null,
      }),
    )
}

export function providerDetail(id: number, overrides: Partial<MangaBakaDetail> = {}): MangaBakaDetail {
  const entry = seriesById(id)
  return {
    providerId: String(entry.mangaBakaId),
    title: entry.title,
    nativeTitle: entry.nativeTitle,
    romanizedTitle: null,
    altTitles: [],
    description: entry.synopsis,
    coverUrl: cover(entry.id),
    year: entry.year,
    type: entry.type.toLowerCase(),
    status: entry.status.toLowerCase(),
    contentRating: 'safe',
    rating: entry.rating * 10,
    sourceRatings: [
      { source: 'anilist', rating: entry.rating * 10 - 2 },
      { source: 'myanimelist', rating: entry.rating * 10 + 1 },
    ],
    totalChapters: entry.chapters,
    finalVolume: null,
    authors: [entry.author],
    artists: [entry.artist],
    publishers: ['Northwind Press'],
    genres: entry.genres,
    tags: entry.tags.map((name, i) => ({ name, weight: String(90 - i * 10), description: null, isSpoiler: i === 2 })),
    links: links(entry),
    malId: entry.mangaBakaId + 200,
    hasAnime: false,
    animeStart: null,
    animeEnd: null,
    readerHint: { score: 91, baseline: 84, readers: 1260 },
    animeResume: null,
    ...overrides,
  }
}

export const animeResume: SeriesAnimeResume = {
  animeTitle: 'Lanterns Over Kasumi',
  services: ['anilist'],
  score: 0.93,
  basis: 'allSeasons',
  coveredTo: 38,
  resumeAt: 39,
  coveredLabel: 'Ch. 38',
  nextLabel: 'Ch. 39',
  nextFrom: 39,
  nextTo: 46,
  readTo: 12,
  resumeChapterId: 1390,
  resumeChapterLabel: 'Ch. 39',
  resumeDownloaded: true,
  unmarkedCount: 26,
}

export const scanStatusDone: SeriesScanStatus = {
  state: 'done',
  queuedAtUtc: hoursAgo(5),
  finishedAtUtc: hoursAgo(5),
  chaptersChecked: 142,
  probed: 24,
  queued: 3,
  skipped: { cutoff_met: 80, trusted: 4 },
}

export function torrentProposal(seriesId: number, overrides: Partial<TorrentProposalDto> = {}): TorrentProposalDto {
  return {
    id: 77,
    seriesId,
    seriesTitle: seriesById(seriesId).title,
    title: `[Northwind] ${seriesById(seriesId).title} v01-v05 (Digital) (1600px)`,
    indexer: 'Nyaa Mirror',
    sizeBytes: 1_480_000_000,
    span: { volumeStart: 1, volumeEnd: 5, chapters: [], wholeSeries: false },
    reasons: ['adds_missing_chapters'],
    upgradeCount: 41,
    alreadyMetCount: 3,
    skippedCount: 2,
    missingCount: 0,
    unknownCount: 0,
    score: 214,
    tier: 'volume',
    status: 'pending',
    createdAtUtc: hoursAgo(9),
    resolvedAtUtc: null,
    queueItemId: null,
    seriesCoverUrl: `/api/v1/mediacover/${seriesId}/cover.jpg`,
    ...overrides,
  }
}

export const releases: ReleaseRowDto[] = [
  {
    guid: 'rel-1',
    title: '[Northwind] Lanterns Over Kasumi v01-v05 (Digital) (1600px)',
    size: 1_480_000_000,
    indexer: 'Nyaa Mirror',
    seeders: 58,
    leechers: 3,
    protocol: 'torrent',
    infoUrl: 'https://indexer.example/view/1',
    parsed: {
      span: { volumeStart: 1, volumeEnd: 5, chapters: [], wholeSeries: false },
      tier: 'volume',
      score: 214,
      verdict: 'proposal',
      reasons: ['adds_missing_chapters'],
      upgradeCount: 41,
      alreadyMetCount: 3,
      missingCount: 2,
      titleMatched: true,
      rivalTitle: null,
    },
  },
  {
    guid: 'rel-2',
    title: 'Lanterns Over Kasumi - Complete (Scans)',
    size: 6_400_000_000,
    indexer: 'Nyaa Mirror',
    seeders: 4,
    leechers: 0,
    protocol: 'torrent',
    infoUrl: null,
    parsed: {
      span: { volumeStart: null, volumeEnd: null, chapters: [], wholeSeries: true },
      tier: 'scanlator',
      score: 70,
      verdict: 'ignore',
      reasons: ['whole_series_pack'],
      upgradeCount: 0,
      alreadyMetCount: 120,
      missingCount: 0,
      titleMatched: true,
      rivalTitle: null,
    },
  },
  {
    guid: 'rel-3',
    title: 'Lanterns Over Kasumi Side Stories v01',
    size: 310_000_000,
    indexer: 'Usenet Gate',
    seeders: null,
    leechers: null,
    protocol: 'usenet',
    infoUrl: 'https://indexer.example/view/3',
    parsed: {
      span: { volumeStart: 1, volumeEnd: 1, chapters: [], wholeSeries: false },
      tier: 'official',
      score: 120,
      verdict: 'ignore',
      reasons: [],
      upgradeCount: 0,
      alreadyMetCount: 0,
      missingCount: 0,
      titleMatched: false,
      rivalTitle: 'Lanterns Over Kasumi: Side Stories',
    },
  },
]

export function relinkPlan(seriesId: number): RelinkPlan {
  const title = seriesById(seriesId).title
  return {
    seriesId,
    moved: 6,
    supersededCount: 1,
    supersededBytes: 124_000_000,
    unrecognized: 1,
    files: [
      {
        relativePath: `${title} v01.cbz`,
        fileName: `${title} v01.cbz`,
        size: 212_000_000,
        label: 'Vol.1',
        isVolume: true,
        recognized: true,
        confidence: 'volumeRange',
        chapters: ['1', '2', '3', '4', '5', '6', '7', '8', '9'],
        gains: [
          { id: 1010, label: 'Ch.1' },
          { id: 1020, label: 'Ch.2' },
          { id: 1030, label: 'Ch.3' },
        ],
        loses: [],
        superseded: false,
        excluded: false,
      },
      {
        relativePath: `${title} - Ch.1.cbz`,
        fileName: `${title} - Ch.1.cbz`,
        size: 18_000_000,
        label: 'Ch.1',
        isVolume: false,
        recognized: true,
        confidence: 'fileName',
        chapters: ['1'],
        gains: [],
        loses: [{ id: 1010, label: 'Ch.1' }],
        superseded: true,
        excluded: false,
      },
      {
        relativePath: 'extras/scan0042.cbz',
        fileName: 'scan0042.cbz',
        size: 9_000_000,
        label: null,
        isVolume: false,
        recognized: false,
        confidence: null,
        chapters: [],
        gains: [],
        loses: [],
        superseded: false,
        excluded: false,
      },
    ],
    chapters: [
      { id: 1010, label: 'Ch.1', number: 1, state: 'movesToVolume', toLabel: 'Vol.1' },
      { id: 1020, label: 'Ch.2', number: 2, state: 'becomesReadable', toLabel: 'Vol.1' },
      { id: 1030, label: 'Ch.3', number: 3, state: 'unchanged', toLabel: null },
      { id: 1040, label: 'Ch.4', number: 4, state: 'availableNotLinked', toLabel: null },
      { id: 1050, label: 'Ch.5', number: 5, state: 'missing', toLabel: null },
    ],
  }
}

export function renamePlan(seriesId: number, overrides: Partial<SeriesRenamePlan> = {}): SeriesRenamePlan {
  const entry = seriesById(seriesId)
  return {
    seriesId,
    title: entry.title,
    folderFrom: entry.title,
    folderTo: `${entry.title} (${entry.year})`,
    folderChanged: true,
    hasChanges: true,
    conflicts: [],
    fingerprint: 'plan-1',
    files: [1, 2, 3, 4].map((n) => ({
      chapterFileId: 700 + n,
      from: `${entry.title} - Ch.${n}.cbz`,
      to: `${entry.title} (${entry.year}) - c00${n}.cbz`,
    })),
    ...overrides,
  }
}

export function compareSnapshot(seriesId: number, overrides: Partial<CompareSnapshot> = {}): CompareSnapshot {
  const page = (n: number, width: number) => ({
    url: `/mock/page/${seriesId}/${n}.svg`,
    width,
    height: Math.round(width * 1.5),
    bytes: 380_000 + n * 20_000,
  })
  return {
    seriesId,
    running: false,
    mixedChapters: false,
    pagesAligned: true,
    chapterNumber: 12,
    commonChapters: [10, 11, 12, 13],
    panels: [
      {
        mappingId: seriesId * 10 + 1,
        sourceName: 'mangadex',
        displayName: 'MangaDex',
        status: 'ready',
        error: null,
        chapterLabel: 'Ch. 12',
        aligned: true,
        pages: [page(1, 1600), page(2, 1600), null],
        pageCount: 26,
        quality: {
          tier: 'official',
          score: 94,
          matchedFormats: [],
          isUpgrade: true,
          reason: null,
          medianWidth: 1600,
          pageCount: 26,
          currentTier: 'aggregator',
          currentScore: 31,
          currentWidth: 800,
          resolutionPoints: 60,
          compressionPoints: 34,
          bitsPerPixel: 1.8,
          currentResolutionPoints: 20,
          currentCompressionPoints: 11,
          currentBitsPerPixel: 0.8,
        },
      },
      {
        mappingId: seriesId * 10 + 2,
        sourceName: 'comick',
        displayName: 'Comick',
        status: 'ready',
        error: null,
        chapterLabel: 'Ch. 12',
        aligned: true,
        pages: [page(1, 1100), page(2, 1100), page(3, 1100)],
        pageCount: 24,
        quality: null,
      },
      {
        mappingId: seriesId * 10 + 3,
        sourceName: 'weebcentral',
        displayName: 'Weeb Central',
        status: 'failed',
        error: 'The source did not answer in time',
        chapterLabel: null,
        aligned: false,
        pages: [],
        pageCount: null,
        quality: null,
      },
    ],
    ...overrides,
  }
}

export const sourceSearchResults = (query: string): SourceSearchResult[] => [
  {
    sourceSeriesId: 'a1',
    title: query,
    url: 'https://comick.example/comic/a1',
    coverUrl: cover(1),
    description: 'A lamplighter who reads memories caught in flame.',
  },
  { sourceSeriesId: 'a2', title: `${query}: Side Stories`, url: 'https://comick.example/comic/a2', coverUrl: cover(5), description: null },
]

export const resolvedSourceUrl: ResolvedSourceUrl = {
  sourceName: 'comick',
  displayName: 'Comick',
  sourceSeriesId: 'a1',
  title: 'Lanterns Over Kasumi',
  url: 'https://comick.example/comic/a1',
  coverUrl: cover(1),
}
