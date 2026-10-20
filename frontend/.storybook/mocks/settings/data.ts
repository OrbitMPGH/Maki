import type {
  BackupInfo,
  BackupRetentionSettings,
  DiscoverSettings,
  DownloadClientKindInfo,
  DownloadClientSettings,
  DownloadSettings,
  DumpProgress,
  ImageCacheInfo,
  IndexerEntry,
  IndexerOptions,
  IndexerSettings,
  KavitaLibrary,
  LibrarySettings,
  MonitoringSettings,
  NameParsingSettings,
  NamingToken,
  OpdsSettings,
  ProgressSettings,
  ProgressSummary,
  ReadFileCleanupSettings,
  RecommendationIndexStatus,
  ScrobbleSettings,
  ScrobbleStatus,
  SourceInfo,
  SourceLanguageSettings,
  SourcePrioritySettings,
} from '../../../src/api/hooks'
import type {
  ApiKey,
  AuthEvent,
  OidcSettings,
  SecuritySettings,
  UserSummary,
} from '../../../src/api/auth'
import type {
  ImportListsStatusDto,
  NotificationDto,
  NotificationProviderDescriptor,
  RootFolder,
  TagDto,
  UpdateSettingsDto,
  UpdateStatusDto,
} from '../../../src/api/types'
import type { InboxPrefs } from '../../../src/api/inbox'
import type { ReaderSettings } from '../../../src/api/reader'
import type { ReadingProfile } from '../../../src/api/readingProfiles'
import type { RecycleBin } from '../../../src/api/recycleBin'
import type { PageConversionStatus } from '../../../src/api/pageConversion'
import type { SuwayomiConnection } from '../../../src/api/suwayomi'
import type { FormatScoreDto, QualityFormatDto, UpgradeProfileDto, UpgradeSettings } from '../../../src/api/upgrades'
import type { ReaderPrefs } from '../../../src/pages/reader/prefs'
import { adminMe, daysAgo, hoursAgo, minutesAgo, readerMe, daysAhead } from '../api'
import { catalogue } from '../shared'

const GB = 1024 ** 3
const MB = 1024 ** 2

export const readerPrefs: ReaderPrefs = {
  mode: 'paged',
  direction: 'rtl',
  fit: 'height',
  pageGap: 0,
  preload: 3,
  tapZones: true,
  showPageNumber: true,
  autoNextChapter: true,
  chapterBanner: true,
  background: '#0a0a0b',
  scale: 100,
  dim: 0,
  filter: 'none',
  keepAwake: true,
  splitWide: 'off',
  cropBorders: false,
  autoWebtoon: true,
}

export const readerSettings: ReaderSettings = {
  defaults: readerPrefs,
  pushToKavita: true,
  kavitaUserId: adminMe.id,
  pullFromKavita: true,
  kavitaLive: 'Connected',
}

export const readingProfiles: ReadingProfile[] = [
  { id: 1, name: 'Manga, right to left', prefs: readerPrefs, seriesTypes: ['manga'] },
  {
    id: 2,
    name: 'Webtoon scroll',
    prefs: { ...readerPrefs, mode: 'vertical', direction: 'ltr', fit: 'width', pageGap: 8, tapZones: false },
    seriesTypes: ['manhwa', 'manhua'],
  },
  {
    id: 3,
    name: 'Night reading',
    prefs: { ...readerPrefs, background: '#000000', dim: 25, filter: 'sepia' },
    seriesTypes: [],
  },
]

export const rootFolders: RootFolder[] = [
  { id: 1, path: '/library/manga', freeSpace: 842 * GB, accessible: true },
  { id: 2, path: '/library/webtoons', freeSpace: 118 * GB, accessible: true },
  { id: 3, path: 'Z:\\old-archive', freeSpace: null, accessible: false },
]

export const tags = [
  { id: 1, label: 'Favourites', color: 'grape', seriesCount: 7 },
  { id: 2, label: 'Weekend read', color: 'teal', seriesCount: 4 },
  { id: 3, label: 'Re-read', color: 'orange', seriesCount: 2 },
] satisfies TagDto[]

const sourceBase = {
  needsFlareSolverr: false,
  supportsLanguageFilter: false,
  enabled: true,
  provider: null,
  suwayomi: null,
} satisfies Partial<SourceInfo>

export const sources: SourceInfo[] = [
  {
    ...sourceBase,
    name: 'mangadex',
    displayName: 'MangaDex',
    baseUrl: 'https://mangadex.example',
    supportsLanguageFilter: true,
    supportedLanguages: ['en', 'es', 'pt-br', 'fr', 'ja'],
    kind: 'aggregator',
    content: ['manga', 'manhwa', 'manhua'],
    rating: 'mature',
    defaultEnabled: true,
    defaultRank: 1,
    quality: { rating: 'good', bitsPerPixel: 1.42, basis: 'library', samples: 318, series: 41 },
  },
  {
    ...sourceBase,
    name: 'comick',
    displayName: 'Comick',
    baseUrl: 'https://comick.example',
    supportsLanguageFilter: true,
    supportedLanguages: ['en', 'es', 'fr'],
    kind: 'aggregator',
    content: ['manga', 'manhwa', 'manhua', 'webtoon'],
    rating: 'mature',
    defaultEnabled: true,
    defaultRank: 2,
    quality: { rating: 'fair', bitsPerPixel: 0.96, basis: 'library', samples: 120, series: 22 },
  },
  {
    ...sourceBase,
    name: 'asurascans',
    displayName: 'Asura Scans',
    baseUrl: 'https://asura.example',
    needsFlareSolverr: true,
    supportedLanguages: ['en'],
    kind: 'scanlator',
    content: ['manhwa', 'webtoon'],
    rating: 'general',
    defaultEnabled: true,
    defaultRank: 3,
    quality: { rating: 'high', bitsPerPixel: 2.1, basis: 'baseline', samples: 0, series: 0 },
  },
  {
    ...sourceBase,
    name: 'weebcentral',
    displayName: 'Weeb Central',
    baseUrl: 'https://weebcentral.example',
    enabled: false,
    supportedLanguages: ['en'],
    kind: 'aggregator',
    content: ['manga'],
    rating: 'general',
    defaultEnabled: false,
    defaultRank: 4,
  },
  {
    ...sourceBase,
    name: 'mangaplus',
    displayName: 'Shonen Plaza',
    baseUrl: 'https://plaza.example',
    supportedLanguages: ['en', 'es', 'pt-br', 'fr', 'de'],
    kind: 'official',
    content: ['manga'],
    rating: 'general',
    defaultEnabled: true,
    defaultRank: 0,
    quality: { rating: 'high', bitsPerPixel: 2.6, basis: 'library', samples: 64, series: 9 },
  },
  {
    ...sourceBase,
    name: 'suwayomi-1001',
    displayName: 'Lantern Reader (EN)',
    baseUrl: 'http://suwayomi:4567',
    provider: 'suwayomi',
    supportedLanguages: ['en'],
    kind: 'aggregator',
    content: ['manga'],
    rating: 'general',
    suwayomi: {
      sourceId: '1001',
      siteName: 'Lantern Reader',
      lang: 'en',
      mixedLanguage: false,
      chapterLanguage: null,
      needsSetup: false,
      missing: false,
    },
  },
  {
    ...sourceBase,
    name: 'suwayomi-1002',
    displayName: 'Lantern Reader (ES)',
    baseUrl: 'http://suwayomi:4567',
    provider: 'suwayomi',
    enabled: false,
    supportedLanguages: ['es'],
    kind: 'aggregator',
    content: ['manga'],
    rating: 'general',
    suwayomi: {
      sourceId: '1002',
      siteName: 'Lantern Reader',
      lang: 'es',
      mixedLanguage: false,
      chapterLanguage: null,
      needsSetup: false,
      missing: false,
    },
  },
  {
    ...sourceBase,
    name: 'suwayomi-2001',
    displayName: 'Multi Archive',
    baseUrl: 'http://suwayomi:4567',
    provider: 'suwayomi',
    enabled: false,
    supportedLanguages: [],
    kind: 'scanlator',
    content: ['manga', 'manhwa'],
    rating: 'general',
    suwayomi: {
      sourceId: '2001',
      siteName: 'Multi Archive',
      lang: 'all',
      mixedLanguage: true,
      chapterLanguage: null,
      needsSetup: true,
      missing: false,
    },
  },
]

export const sourcePriority: SourcePrioritySettings = {
  order: ['mangadex', 'comick', 'asurascans', 'mangaplus', 'weebcentral', 'suwayomi-1001', 'suwayomi-1002', 'suwayomi-2001'],
  disabled: ['weebcentral', 'suwayomi-1002', 'suwayomi-2001'],
}

export const sourceLanguages: SourceLanguageSettings = {
  order: ['en', 'es', 'pt-br', 'fr', 'ja', 'de'],
  disabled: ['fr', 'de'],
  available: ['en', 'es', 'pt-br', 'fr', 'ja', 'de'],
}

export const suwayomiConnection: SuwayomiConnection = {
  url: 'http://suwayomi:4567',
  authMode: 'basic',
  username: 'maki',
  passwordSet: true,
}

export const downloadSettings: DownloadSettings = {
  concurrentChapters: 2,
  retryEnabled: true,
  retryMaxAttempts: 5,
  smartDownloadChaptersLeft: 5,
  smartDownloadChapters: 10,
  itemTimeoutMinutes: 120,
  useHardlinks: true,
  bulkHoldThreshold: 5,
  sourceOrder: 'manual',
  scoutOnMatch: false,
  adaptiveRefresh: true,
}

export const readFileCleanup: ReadFileCleanupSettings = { enabled: false, days: 7, keepLast: true }

export const downloadClientKinds: DownloadClientKindInfo[] = [
  { kind: 'qbittorrent', name: 'qBittorrent', usesUsername: true, defaultUrl: 'http://localhost:8080' },
  { kind: 'transmission', name: 'Transmission', usesUsername: true, defaultUrl: 'http://localhost:9091/transmission' },
  { kind: 'deluge', name: 'Deluge', usesUsername: false, defaultUrl: 'http://localhost:8112' },
]

export const downloadClient: DownloadClientSettings = {
  kind: 'qbittorrent',
  url: 'http://192.168.1.20:8080',
  username: 'maki',
  password: 'hunter2-example',
  label: 'maki',
  pathMapFrom: '/downloads',
  pathMapTo: 'Z:\\downloads',
}

export const indexerProwlarr: IndexerSettings = {
  backend: 'prowlarr',
  url: 'http://192.168.1.20:9696',
  apiKey: 'prowlarr-example-key',
}

export const indexerJackett: IndexerSettings = { backend: 'jackett', url: null, apiKey: null }

export const indexerOptions: IndexerOptions = { indexerIds: 'nyaa,fakebay', categories: '3030' }

export const indexerList: IndexerEntry[] = [
  { id: 'nyaa', name: 'Nyaa Mirror', enabled: true, protocol: 'torrent', categories: [{ id: 3030, name: 'Books/Comics' }, { id: 3010, name: 'Books/Ebook' }] },
  { id: 'fakebay', name: 'Fakebay', enabled: true, protocol: 'torrent', categories: [{ id: 3030, name: 'Books/Comics' }] },
  { id: 'tokyoarchive', name: 'Tokyo Archive', enabled: false, protocol: 'torrent', categories: [{ id: 3030, name: 'Books/Comics' }] },
]

export const flaresolverr = { url: 'http://192.168.1.10:8191' }

export const kavitaConnection = {
  url: 'http://192.168.1.20:5000',
  apiKey: 'kavita-example-key',
  pathMapFrom: '/library',
  pathMapTo: '/manga',
  userId: adminMe.id as number | null,
}

export const kavitaLibraries: KavitaLibrary[] = [
  { id: 1, name: 'Manga' },
  { id: 2, name: 'Webtoons' },
  { id: 3, name: 'Light novels' },
]

export const upgradeSettings: UpgradeSettings = {
  enabled: true,
  defaultProfileId: 1,
  scanHour: 4,
  maxPerDay: 25,
  maxProbesPerRun: 50,
  quietPeriodDays: 7,
  trashRetentionDays: 14,
  scanIncognito: false,
  volumeSearch: true,
  torrentAutoGrabMaxBytes: 500 * MB,
  volumeMissingTolerance: 3,
  volumeSearchesPerRun: 10,
  proposalExpiryDays: 30,
}

export const qualityFormats: QualityFormatDto[] = [
  {
    id: 1,
    name: 'Official digital',
    conditions: [
      { type: 'sourceKindIs', value: 'official', required: true, negate: false },
      { type: 'minWidth', value: '1200', required: false, negate: false },
    ],
    version: 1,
    profileCount: 2,
  },
  {
    id: 2,
    name: 'Lossless scans',
    conditions: [
      { type: 'imageFormatIs', value: 'png', required: true, negate: false },
      { type: 'groupMatches', value: '^(Lumen|Hoshi)', required: false, negate: false },
    ],
    version: 3,
    profileCount: 1,
  },
  { id: 3, name: 'Tiny files', conditions: [{ type: 'minBytesPerPage', value: '80000', required: true, negate: true }], version: 1, profileCount: 0 },
]

const formatScores: FormatScoreDto[] = [
  { formatId: 1, score: 40 },
  { formatId: 2, score: 25 },
]

export const upgradeProfiles: UpgradeProfileDto[] = [
  {
    id: 1,
    name: 'Balanced',
    description: 'Prefers official releases but accepts good scanlations.',
    tiers: [
      { tier: 'volume', allowed: true, grouped: false },
      { tier: 'official', allowed: true, grouped: false },
      { tier: 'scanlator', allowed: true, grouped: true },
      { tier: 'aggregator', allowed: true, grouped: false },
      { tier: 'unknown', allowed: false, grouped: false },
    ],
    cutoff: 'official',
    upgradesEnabled: true,
    minScoreDelta: 10,
    maxTierScoreDrop: 30,
    upgradeUntilScore: 0,
    formatScores,
    resolutionWeight: 20,
    compressionWeight: 10,
    pageTolerancePercent: 5,
    allowReplacingUnknown: true,
    version: 2,
    seriesCount: 9,
  },
  {
    id: 2,
    name: 'Storage saver',
    description: null,
    tiers: [
      { tier: 'aggregator', allowed: true, grouped: false },
      { tier: 'scanlator', allowed: true, grouped: false },
      { tier: 'official', allowed: false, grouped: false },
      { tier: 'volume', allowed: false, grouped: false },
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
    seriesCount: 0,
  },
]

export const libraryDefaults: LibrarySettings = {
  writeComicInfo: true,
  folderNamingMode: 'rename',
  incognitoByRating: { safe: 'Off', suggestive: 'Off', erotica: 'ScrobbleOnly', pornographic: 'Full' },
  writeCoverToFolder: false,
  seriesFolderFormat: '{Series TitleYear}',
  chapterFormat: '{Series Title} - c{Chapter:000}',
  renameImportedFiles: true,
  newSeriesMonitorMode: 'Smart',
}

export const monitoring: MonitoringSettings = { unmonitorSpecials: false, defaultMode: 'Smart' }
export const discoverSettings: DiscoverSettings = { maxContentRating: 'suggestive' }

export const nameParsing: NameParsingSettings = {
  languages: ['fr'],
  available: [
    { code: 'fr', volumeWords: ['Tome', 'T'], chapterWords: ['Chapitre', 'Ch'] },
    { code: 'es', volumeWords: ['Tomo'], chapterWords: ['Capitulo', 'Cap'] },
    { code: 'de', volumeWords: ['Band'], chapterWords: ['Kapitel', 'Kap'] },
  ],
}

export const namingTokens: NamingToken[] = [
  { token: '{Series Title}', category: 'Series', description: 'The series title.', example: 'Lanterns Over Kasumi' },
  { token: '{Series TitleYear}', category: 'Series', description: 'The series title followed by its start year.', example: 'Lanterns Over Kasumi (2021)' },
  { token: '{Series Author}', category: 'Series', description: 'The first listed author.', example: 'Aoi Mizuhara' },
  { token: '{Chapter}', category: 'Chapter', description: 'The chapter number. Add a format such as :000 to pad it.', example: '12' },
  { token: '{Volume}', category: 'Chapter', description: 'The volume number when known.', example: '3' },
  { token: '{Chapter Title}', category: 'Chapter', description: 'The chapter title when the source has one.', example: 'The Valley Forgets' },
  { token: '{Source}', category: 'Release', description: 'The source the chapter came from.', example: 'MangaDex' },
]

export const dumpProgressIdle: DumpProgress = {
  running: false,
  phase: 'idle',
  downloadedBytes: 0,
  totalBytes: null,
  bytesPerSecond: null,
  estimatedSecondsRemaining: null,
  startedAt: null,
  finishedAt: hoursAgo(9),
  lastInstalled: true,
  lastError: null,
}

export const dumpProgressDownloading: DumpProgress = {
  running: true,
  phase: 'downloading',
  downloadedBytes: 1_100 * MB,
  totalBytes: 1_800 * MB,
  bytesPerSecond: 14 * MB,
  estimatedSecondsRemaining: 50,
  startedAt: minutesAgo(2),
  finishedAt: null,
  lastInstalled: false,
  lastError: null,
}

export const recommendationIndex: RecommendationIndexStatus = {
  modelPresent: true,
  dumpPresent: true,
  vectorCount: 168_204,
  recommendableTotal: 171_930,
  running: false,
  phase: 'idle',
  embedded: 0,
  scanned: 0,
  startedAt: null,
  finishedAt: daysAgo(2),
  lastEmbedded: 168_204,
  lastError: null,
  estimatedSecondsRemaining: null,
  prebuiltEnabled: true,
  prebuiltInstalledAt: daysAgo(2),
  embeddingModel: 'base',
  useFullDump: false,
  modelSwitching: false,
  modelSwitchError: null,
}

const u = (id: number, userName: string, displayName: string | null, extra: Partial<UserSummary> = {}): UserSummary => ({
  ...readerMe,
  id,
  userName,
  displayName,
  oidcLinked: false,
  twoFactorEnabled: false,
  disabled: false,
  pendingSetup: false,
  createdAt: daysAgo(200 - id * 20),
  lastLoginAt: daysAgo(id),
  ...extra,
})

export const users: UserSummary[] = [
  { ...adminMe, disabled: false, pendingSetup: false, createdAt: daysAgo(400), lastLoginAt: hoursAgo(1), twoFactorEnabled: true },
  u(2, 'reader', 'Rin', { rootFolderIds: [1], allRootFolders: false }),
  u(3, 'noa', 'Noa', {
    permissions: 'AddSeries, DownloadChapters, UseTrackers, UseOpds',
    permissionNames: ['AddSeries', 'DownloadChapters', 'UseTrackers', 'UseOpds'],
    maxContentRating: 'Erotica',
    allRootFolders: true,
    rootFolderIds: [],
    oidcLinked: true,
  }),
  u(4, 'guest', null, { disabled: true, lastLoginAt: null }),
  u(5, 'new-friend', 'Sora', { pendingSetup: true, lastLoginAt: null }),
]

export const authEvents: AuthEvent[] = [
  { timestamp: minutesAgo(12), type: 'LoginSucceeded', userId: 1, userName: 'maki', clientIp: '192.168.1.40', userAgent: 'Firefox/130', detail: null },
  { timestamp: hoursAgo(3), type: 'LoginFailed', userId: null, userName: 'admin', clientIp: '203.0.113.9', userAgent: 'curl/8.4', detail: 'Unknown user' },
  { timestamp: hoursAgo(5), type: 'LockedOut', userId: 3, userName: 'noa', clientIp: '198.51.100.22', userAgent: 'Chrome/128', detail: 'Too many attempts' },
  { timestamp: daysAgo(1), type: 'TwoFactorEnabled', userId: 1, userName: 'maki', clientIp: '192.168.1.40', userAgent: 'Firefox/130', detail: null },
]

export const securitySettings: SecuritySettings = {
  requireHttps: false,
  trustedProxies: '172.18.0.0/16',
  lockoutMaxAttempts: 5,
  lockoutMinutes: 15,
  sessionDays: 30,
}

export const oidcSettings: OidcSettings = {
  enabled: true,
  authority: 'https://sso.example.test/application/o/maki/',
  clientId: 'maki-client',
  clientSecret: '',
  scopes: 'openid profile email',
  displayName: 'Authentik',
  oidcOnly: false,
  autoProvision: true,
  usernameClaim: 'preferred_username',
  adminClaim: 'groups:maki-admins',
  permissionClaim: '',
  redirectPath: '/signin-oidc',
  breakGlassActive: false,
}

export const apiKeys: ApiKey[] = [
  { id: 1, name: 'Reading app', prefix: 'mk_a1b2', scope: 'Opds', createdAt: daysAgo(60), lastUsedAt: hoursAgo(2), revokedAt: null },
  { id: 2, name: 'Home automation', prefix: 'mk_c3d4', scope: 'Full', createdAt: daysAgo(20), lastUsedAt: null, revokedAt: null },
  { id: 3, name: 'Old laptop', prefix: 'mk_e5f6', scope: 'Full', createdAt: daysAgo(300), lastUsedAt: daysAgo(120), revokedAt: daysAgo(100) },
]

export const twoFactorOff = {
  enabled: false,
  hasAuthenticator: false,
  recoveryCodesLeft: 0,
  available: true,
  hasPassword: true,
  ssoDelegated: false,
}

export const twoFactorOn = { ...twoFactorOff, enabled: true, hasAuthenticator: true, recoveryCodesLeft: 8 }

export const opdsOff: OpdsSettings = { enabled: false, trackProgress: true, hasToken: false, tokenPrefix: null, feedUrl: null, komga: false }
export const opdsOn: OpdsSettings = { enabled: true, trackProgress: true, hasToken: true, tokenPrefix: 'a91f3c', feedUrl: null, komga: false }

export const inboxPrefs: InboxPrefs = {
  types: {
    newChapterAvailable: true,
    smartDownloadQueued: true,
    chapterDownloaded: false,
    downloadFailed: true,
    achievementUnlocked: true,
    levelUp: true,
    requestSubmitted: true,
    requestApproved: true,
    requestRejected: true,
    requestEdited: true,
    healthIssue: true,
    updateAvailable: true,
    importFinished: true,
    backupFinished: false,
    sourceMatchFinished: true,
    importListFinished: true,
    followedCreatorRelease: true,
    chapterUpgraded: true,
    upgradeRestoreFailed: true,
    torrentProposalPending: true,
    volumeUpgraded: true,
    accountSecurity: true,
    seriesStatusChanged: true,
  },
  toasts: true,
  seriesDefault: 'All',
}

export const progressSettings: ProgressSettings = {
  enabled: true,
  showStreaks: true,
  showOnLeaderboard: true,
  timeZone: 'Europe/Stockholm',
}

export const progressSummary: ProgressSummary = {
  enabled: true,
  showStreaks: true,
  level: { level: 14, xp: 5230, intoLevel: 230, levelSpan: 600, nextLevelXp: 5600, progress: 0.38 },
  chaptersRead: 1284,
  readingSeconds: 612_000,
  seriesFinished: 23,
  daysRead: 141,
  currentStreak: 6,
  longestStreak: 31,
  earned: 12,
  total: 40,
  recent: [],
  goals: [
    { id: 1, period: 'Week', metric: 'Chapters', target: 40, progress: 27 },
    { id: 2, period: 'Month', metric: 'SeriesFinished', target: 2, progress: 2 },
  ],
}

export const scrobbleSettings: ScrobbleSettings = {
  aniListClientId: '48211',
  aniListClientSecret: null,
  malClientId: null,
  malClientSecret: null,
  mangaBakaToken: null,
  kitsuClientId: null,
  kitsuClientSecret: null,
  kitsuEmail: 'rin@example.test',
  kitsuPassword: null,
  mangaUpdatesUsername: null,
  mangaUpdatesPassword: null,
  kitsuPasswordSet: true,
  mangaUpdatesPasswordSet: false,
  mangaBakaTokenSet: false,
  intervalMinutes: 30,
  planToRead: false,
  libraryIds: '1',
  isAdmin: true,
}

export const scrobbleStatus: ScrobbleStatus = {
  connections: [
    { service: 'anilist', label: 'AniList', configured: true, connected: true, username: 'rin_reads', oAuth: true, syncReading: true, syncRatings: true, animeList: true, animeSignals: false },
    { service: 'mal', label: 'MyAnimeList', configured: false, connected: false, username: null, oAuth: true, syncReading: false, syncRatings: false, animeList: true, animeSignals: false },
    { service: 'mangabaka', label: 'MangaBaka', configured: false, connected: false, username: null, oAuth: false, syncReading: false, syncRatings: false, animeList: false, animeSignals: false },
    { service: 'kitsu', label: 'Kitsu', configured: true, connected: true, username: 'rin@example.test', oAuth: false, syncReading: true, syncRatings: false, animeList: true, animeSignals: false },
  ],
  running: false,
  lastSyncAt: minutesAgo(18),
  nextSyncAt: daysAhead(0),
  intervalMinutes: 30,
  planToRead: false,
  recent: [],
  unmatched: [],
  log: [],
}

export const importListsSettings = { enabled: true, intervalMinutes: 60 }

export const importListsStatus: ImportListsStatusDto = {
  enabled: true,
  intervalMinutes: 60,
  trackers: [
    {
      service: 'anilist',
      label: 'AniList',
      connected: true,
      prefs: { enabled: true, statuses: ['Reading', 'PlanToRead'], rootFolderId: 1, monitored: true, monitorNewItems: 'Smart', maxPerRun: 25 },
      lastRun: { at: hoursAgo(2), added: 3, requested: 0, skipped: 4, errors: 0, dumpUnavailable: false },
    },
    {
      service: 'mal',
      label: 'MyAnimeList',
      connected: false,
      prefs: { enabled: false, statuses: ['Reading'], rootFolderId: null, monitored: true, monitorNewItems: 'All', maxPerRun: 25 },
      lastRun: null,
    },
  ],
  skipped: [
    { id: 1, service: 'anilist', remoteId: '5521', title: 'Cloudless Archipelago', reason: 'Unmatched', createdAt: hoursAgo(2) },
    { id: 2, service: 'anilist', remoteId: '5530', title: 'Weekend Alchemist', reason: 'Removed', createdAt: daysAgo(1) },
  ],
}

export const providers: NotificationProviderDescriptor[] = [
  { type: 'Discord', supportsPoster: true, fields: [{ key: 'webhookUrl', kind: 'Url', required: true, placeholder: 'https://discord.com/api/webhooks/...' }] },
  { type: 'Webhook', supportsPoster: false, fields: [{ key: 'url', kind: 'Url', required: true, placeholder: 'https://example.test/hook' }] },
  {
    type: 'Ntfy',
    supportsPoster: false,
    fields: [
      { key: 'serverUrl', kind: 'Url', required: true, placeholder: 'https://ntfy.example.test' },
      { key: 'topic', kind: 'Text', required: true, placeholder: null },
      { key: 'token', kind: 'Secret', required: false, placeholder: null },
      { key: 'priority', kind: 'Number', required: false, placeholder: null, min: 1, max: 5 },
    ],
  },
  {
    type: 'Telegram',
    supportsPoster: true,
    fields: [
      { key: 'botToken', kind: 'Secret', required: true, placeholder: null },
      { key: 'chatId', kind: 'Text', required: true, placeholder: null },
      { key: 'silent', kind: 'Boolean', required: false, placeholder: null },
    ],
  },
]

const allEvents = {
  chapterDownloaded: true,
  downloadFailed: true,
  newChapterAvailable: true,
  importCompleted: false,
  healthIssue: true,
  updateAvailable: false,
  seriesAdded: false,
  seriesRemoved: false,
  requestSubmitted: false,
  requestResolved: false,
  manualMatchNeeded: true,
  seriesStatusChanged: false,
}

export const notificationConnections: NotificationDto[] = [
  {
    id: 1,
    name: 'Reading room Discord',
    type: 'Discord',
    enabled: true,
    config: { webhookUrl: 'https://discord.com/api/webhooks/000/example' },
    events: allEvents,
    tagIds: [],
    lastAttemptAt: minutesAgo(30),
    lastSuccessAt: minutesAgo(30),
    lastError: null,
    consecutiveFailures: 0,
  },
  {
    id: 2,
    name: 'Phone alerts',
    type: 'Ntfy',
    enabled: true,
    config: { serverUrl: 'https://ntfy.example.test', topic: 'maki-alerts', priority: '3' },
    events: { ...allEvents, chapterDownloaded: false, seriesAdded: true },
    tagIds: [1],
    lastAttemptAt: hoursAgo(1),
    lastSuccessAt: daysAgo(2),
    lastError: 'status:404',
    consecutiveFailures: 4,
  },
  {
    id: 3,
    name: 'Home Assistant',
    type: 'Webhook',
    enabled: true,
    config: { url: 'http://homeassistant.local/api/webhook/maki' },
    events: allEvents,
    tagIds: [],
    lastAttemptAt: null,
    lastSuccessAt: null,
    lastError: null,
    consecutiveFailures: 0,
  },
  {
    id: 4,
    name: 'Retired channel',
    type: 'Webhook',
    enabled: false,
    config: { url: 'https://example.test/hooks/old' },
    events: allEvents,
    tagIds: [],
    lastAttemptAt: daysAgo(90),
    lastSuccessAt: daysAgo(90),
    lastError: null,
    consecutiveFailures: 0,
  },
]

export const backups: BackupInfo[] = [
  { name: 'maki-2026-10-19-0300.zip', sizeBytes: 48 * MB, manifest: { appVersion: '0.32.0', createdUtc: hoursAgo(9), lastMigration: '20261012_AddUpgradeProfiles', kind: 'scheduled' } },
  { name: 'maki-2026-10-12-0300.zip', sizeBytes: 44 * MB, manifest: { appVersion: '0.31.2', createdUtc: daysAgo(7), lastMigration: '20260930_AddRecycleBin', kind: 'scheduled' } },
  { name: 'maki-pre-update-0.32.0.zip', sizeBytes: 43 * MB, manifest: { appVersion: '0.31.2', createdUtc: daysAgo(8), lastMigration: null, kind: 'manual' } },
]

export const backupSettings: BackupRetentionSettings = { retention: 7, scheduled: true }

export const imageCache: ImageCacheInfo = {
  status: {
    running: false,
    phase: 'idle',
    force: false,
    processed: 0,
    total: 0,
    downloaded: 0,
    failed: 0,
    skipped: 0,
    thumbnailsCleared: 0,
    startedAt: null,
    finishedAt: daysAgo(3),
    lastError: null,
  },
  usage: { coverFiles: 96, coverBytes: 212 * MB, thumbnailFiles: 4120, thumbnailBytes: 640 * MB, seriesTotal: 98, coversMissing: 2 },
}

export const updateStatusCurrent: UpdateStatusDto = {
  currentVersion: '0.32.0',
  isDevBuild: false,
  isDocker: true,
  updateAvailable: false,
  latestVersion: '0.32.0',
  releaseUrl: null,
  releaseNotes: null,
  checkedAt: hoursAgo(6),
}

export const updateStatusAvailable: UpdateStatusDto = {
  ...updateStatusCurrent,
  updateAvailable: true,
  latestVersion: '0.33.0',
  releaseUrl: 'https://example.test/releases/0.33.0',
  releaseNotes: "## What's new\n\n- Offline reading keeps your place\n- Faster library imports\n\n## Fixes\n\n- The reader no longer jumps on rotate",
}

export const updateSettings: UpdateSettingsDto = { checkForUpdates: true }

export const pageConversionIdle: PageConversionStatus = {
  settings: { format: 'webp', webpQuality: 80, avifQuality: 55 },
  pass: {
    state: 'idle',
    hold: null,
    currentFile: null,
    totals: {
      startedAtUtc: daysAgo(3),
      finishedAtUtc: daysAgo(2),
      converted: 412,
      unchanged: 1288,
      hardlinked: 37,
      failed: 2,
      savedBytes: 9.4 * GB,
    },
  },
  library: { convertedFiles: 412, savedBytes: 9.4 * GB, pendingFiles: 64, trashBytes: 21.7 * GB, trashFiles: 412, trashDays: 14 },
}

export const pageConversionRunning: PageConversionStatus = {
  settings: { format: 'avif', webpQuality: 80, avifQuality: 55 },
  pass: {
    state: 'running',
    hold: null,
    currentFile: 'Lanterns Over Kasumi (2021)/Lanterns Over Kasumi - c040.cbz',
    totals: { startedAtUtc: minutesAgo(40), finishedAtUtc: null, converted: 118, unchanged: 9, hardlinked: 0, failed: 0, savedBytes: 1.6 * GB },
  },
  library: { convertedFiles: 118, savedBytes: 1.6 * GB, pendingFiles: 1530, trashBytes: 6.1 * GB, trashFiles: 118, trashDays: 14 },
}

export const pageConversionOff: PageConversionStatus = {
  settings: { format: 'off', webpQuality: 80, avifQuality: 55 },
  pass: { state: 'idle', hold: null, currentFile: null, totals: null },
  library: { convertedFiles: 0, savedBytes: 0, pendingFiles: 1739, trashBytes: 0, trashFiles: 0, trashDays: 14 },
}

export const recycleBin: RecycleBin = {
  retentionDays: 14,
  totalBytes: 312 * MB,
  entries: [
    {
      id: 1,
      seriesId: 1,
      seriesTitle: catalogue[0].title,
      seriesExists: true,
      fileName: 'Lanterns Over Kasumi - c131.cbz',
      relativePath: 'Lanterns Over Kasumi (2021)/Lanterns Over Kasumi - c131.cbz',
      size: 38 * MB,
      reason: 'deleteFile',
      deletedAt: daysAgo(2),
      deletedBy: 'Maki',
      expiresAt: daysAhead(12),
      missing: false,
      chapters: [{ number: 131, volume: 14, language: 'en' }],
    },
    {
      id: 2,
      seriesId: 99,
      seriesTitle: 'Cloudless Archipelago',
      seriesExists: false,
      fileName: 'Cloudless Archipelago (whole series).cbz',
      relativePath: 'Cloudless Archipelago (2020)',
      size: 274 * MB,
      reason: 'seriesDelete',
      deletedAt: daysAgo(10),
      deletedBy: 'Rin',
      expiresAt: daysAhead(4),
      missing: false,
      chapters: [
        { number: 1, volume: 1, language: 'en' },
        { number: 2, volume: 1, language: 'en' },
      ],
    },
    {
      id: 3,
      seriesId: 3,
      seriesTitle: catalogue[2].title,
      seriesExists: true,
      fileName: 'Iron Petal Academy - c12.cbz',
      relativePath: 'Iron Petal Academy (2022)/Iron Petal Academy - c12.cbz',
      size: 0,
      reason: 'removeChapter',
      deletedAt: daysAgo(5),
      deletedBy: null,
      expiresAt: daysAhead(9),
      missing: true,
      chapters: [{ number: 12, volume: null, language: 'en' }],
    },
  ],
}

export const pushPrefs = {
  types: ['newChapterAvailable', 'downloadFailed', 'requestApproved'],
}
