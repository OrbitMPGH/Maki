import { http, HttpResponse } from 'msw'
import type {
  QueueFailureGroupDto,
  QueueHistoryDto,
  QueueItemDto,
  QueuePauseDto,
  TorrentImportPlanDto,
} from '../../../src/api/types'
import type {
  CutoffUnmetPageDto,
  CutoffUnmetRowDto,
  QualitySnapshotDto,
  TorrentProposalDto,
  UpgradeHistoryPageDto,
  UpgradeHistoryRowDto,
  UpgradeSettings,
  UpgradesSummaryDto,
} from '../../../src/api/upgrades'
import { api, daysAgo, hoursAgo, minutesAgo } from '../api'
import { catalogue } from '../shared'
import { sources, title } from './common'

let nextId = 100
const item = (seriesId: number, over: Partial<QueueItemDto>): QueueItemDto => ({
  id: nextId++,
  chapterId: 5000 + nextId,
  seriesId,
  seriesTitle: title(seriesId),
  releaseTitle: null,
  chapterTitle: null,
  chapterVolume: null,
  chapterNumber: '1',
  sourceName: 'mangadex',
  status: 'Queued',
  pagesTotal: 0,
  pagesDone: 0,
  retryCount: 0,
  nextAttempt: null,
  errorKey: null,
  errorParams: null,
  errorMessage: null,
  queuedAt: minutesAgo(12),
  completedAt: null,
  origin: 'manual',
  upgrade: null,
  seriesCoverUrl: `/api/v1/mediacover/${seriesId}/cover.jpg`,
  ...over,
})

const snapshot = (over: Partial<QualitySnapshotDto>): QualitySnapshotDto => ({
  tier: 'aggregator',
  sourceName: 'mangadex',
  group: null,
  pageCount: 24,
  medianWidth: 969,
  medianHeight: 1400,
  imageFormat: 'jpg',
  sizeBytes: 18_400_000,
  score: 0,
  ...over,
})

export const downloadingItem = item(1, {
  chapterNumber: '143',
  status: 'Downloading',
  pagesTotal: 22,
  pagesDone: 14,
  origin: 'monitorrefresh',
})
export const resolvingItem = item(9, { chapterNumber: '23', status: 'Resolving', sourceName: '?' })
export const packagingItem = item(11, {
  chapterNumber: '58',
  status: 'Packaging',
  sourceName: 'comick',
  pagesTotal: 30,
  pagesDone: 30,
})
export const queuedItems: QueueItemDto[] = [
  item(1, { chapterNumber: '144', origin: 'monitorrefresh' }),
  item(6, { chapterNumber: '205', sourceName: 'asura', origin: 'smartdownload' }),
  item(9, { chapterNumber: '24', sourceName: 'weebcentral', chapterVolume: 3 }),
]
export const rateLimitedItem = item(6, {
  chapterNumber: '206',
  sourceName: 'asura',
  status: 'RateLimited',
  nextAttempt: minutesAgo(-6),
  errorKey: 'error.download.rateLimited',
  errorParams: { source: 'Asura Scans' },
})
export const failedItems: QueueItemDto[] = [
  item(4, {
    chapterNumber: '31',
    status: 'Failed',
    retryCount: 3,
    nextAttempt: minutesAgo(-40),
    errorKey: 'error.download.noPages',
    pagesTotal: 18,
    pagesDone: 0,
  }),
  item(7, {
    chapterNumber: '12',
    sourceName: 'comick',
    status: 'Failed',
    retryCount: 1,
    errorKey: 'error.download.challengeNotSolved',
  }),
  item(5, {
    chapterNumber: null,
    chapterTitle: 'Epilogue: The Quiet Tide',
    status: 'Failed',
    errorMessage: 'HTTP 503 from upstream while fetching page 7',
  }),
]
export const awaitingImportItem = item(7, {
  chapterNumber: null,
  releaseTitle: 'Clockwork Tea House v01-v05 [Digital] [Maki Scans]',
  sourceName: 'torrent:Nyaa Mirror',
  status: 'AwaitingImport',
  origin: 'upgrade',
  queuedAt: hoursAgo(3),
})
export const guardedItem = item(2, {
  chapterNumber: null,
  releaseTitle: 'The Ninth Kitchen v10 [Retail]',
  sourceName: 'torrent:Nyaa Mirror',
  status: 'AwaitingImport',
  origin: 'upgrade',
  errorKey: 'error.upgrades.volumeGuard',
  errorParams: { file: 'The Ninth Kitchen v10.cbz', reason: 'fewer_pages' },
})
export const upgradeItems: QueueItemDto[] = [
  item(10, {
    chapterNumber: '14',
    origin: 'upgrade',
    status: 'Downloading',
    pagesTotal: 20,
    pagesDone: 6,
    upgrade: {
      outcome: 'pending',
      reason: null,
      before: snapshot({ score: 0 }),
      predicted: snapshot({ tier: 'scanlator', sourceName: 'asura', medianWidth: 1400, score: 12 }),
      after: null,
      historyId: null,
      historyGroupId: null,
      reverted: false,
      trashAvailable: false,
      force: false,
    },
  }),
  item(12, {
    chapterNumber: '40',
    sourceName: 'comick',
    origin: 'upgrade',
    status: 'Completed',
    pagesTotal: 24,
    pagesDone: 24,
    upgrade: {
      outcome: 'rejected',
      reason: 'fewer_pages',
      before: snapshot({ score: 4 }),
      predicted: snapshot({ tier: 'official', score: 9 }),
      after: snapshot({ tier: 'official', pageCount: 19, score: 9 }),
      historyId: null,
      historyGroupId: null,
      reverted: false,
      trashAvailable: false,
      force: false,
    },
  }),
  item(8, {
    chapterNumber: '88',
    status: 'Queued',
    upgrade: {
      outcome: 'pending',
      reason: null,
      before: snapshot({ score: 2 }),
      predicted: null,
      after: null,
      historyId: null,
      historyGroupId: null,
      reverted: false,
      trashAvailable: false,
      force: true,
    },
  }),
]

export const queueItems: QueueItemDto[] = [
  downloadingItem,
  item(6, { chapterNumber: '204', sourceName: 'asura', status: 'Downloading', pagesTotal: 18, pagesDone: 5 }),
  resolvingItem,
  packagingItem,
  ...queuedItems,
  rateLimitedItem,
  awaitingImportItem,
  guardedItem,
  ...upgradeItems,
  ...failedItems,
]

export const queuePage = (items: QueueItemDto[], total = items.length): QueueHistoryDto => ({
  items,
  total,
  page: 1,
  pageSize: 200,
})

/** A long series title and a decimal chapter number, to see how the table wraps. */
export const longTitleItem = item(9, {
  seriesTitle: 'Grave Robber of the Seventh Hall: The Unabridged Account of the Lamp Keepers and Their Many Debts',
  chapterNumber: '22.5',
  chapterTitle: 'Extra',
  status: 'Queued',
})

const historyRow = (index: number): QueueItemDto => {
  const s = catalogue[index % catalogue.length]
  const cancelled = index % 9 === 4
  const failed = !cancelled && index % 11 === 7
  return item(s.id, {
    chapterNumber: String(s.chapters - (index % 4)),
    sourceName: sources[index % sources.length].name,
    status: cancelled ? 'Cancelled' : failed ? 'Failed' : 'Completed',
    errorKey: failed ? 'error.download.network' : null,
    pagesTotal: 20 + (index % 7),
    pagesDone: 20 + (index % 7),
    queuedAt: hoursAgo(2 + index * 3),
    completedAt: hoursAgo(1 + index * 3),
    origin: index % 5 === 0 ? 'smartdownload' : 'monitorrefresh',
  })
}
export const historyItems: QueueItemDto[] = Array.from({ length: 40 }, (_, i) => historyRow(i))

export const noPause: QueuePauseDto = { all: null, sources: [] }
export const pausedAll: QueuePauseDto = { all: { until: null }, sources: [] }
export const pausedSources: QueuePauseDto = {
  all: null,
  sources: [
    { sourceName: 'weebcentral', until: minutesAgo(-90) },
    { sourceName: 'asura', until: null },
  ],
}

export const failureGroups: QueueFailureGroupDto[] = [
  { reasonKey: 'error.download.noPages', sourceName: 'mangadex', count: 4 },
  { reasonKey: 'error.download.challengeNotSolved', sourceName: 'comick', count: 2 },
  { reasonKey: null, sourceName: 'weebcentral', count: 1 },
  { reasonKey: 'error.upgrades.volumeGuard', sourceName: 'torrent', count: 1 },
]
export const singleFailureGroup: QueueFailureGroupDto[] = [
  { reasonKey: 'error.download.noPages', sourceName: 'mangadex', count: 3 },
]

export const importPlan = (queueItemId: number, over: Partial<TorrentImportPlanDto> = {}): TorrentImportPlanDto => ({
  parsingFingerprint: 'fp-7c1a',
  queueItemId,
  seriesId: 7,
  seriesTitle: title(7),
  releaseName: 'Clockwork Tea House v01-v05 [Digital] [Maki Scans]',
  files: [
    {
      fileName: 'Clockwork Tea House v01.cbz',
      size: 182_000_000,
      label: 'Vol.1',
      chapters: ['1', '2', '3', '4', '5', '6'],
      newChapters: ['5', '6'],
      replaces: [
        { chapterFileId: 901, relativePath: 'Clockwork Tea House/Ch.1-4.cbz', size: 61_000_000, chapters: ['1', '2', '3', '4'] },
      ],
      upgradeCount: 4,
      alreadyMetCount: 0,
    },
    {
      fileName: 'Clockwork Tea House v02.cbz',
      size: 176_000_000,
      label: 'Vol.2',
      chapters: Array.from({ length: 14 }, (_, i) => String(7 + i)),
      newChapters: [],
      replaces: [
        { chapterFileId: 902, relativePath: 'Clockwork Tea House/Ch.7-20.cbz', size: 140_000_000, chapters: ['7', '8'] },
        { chapterFileId: 903, relativePath: 'Clockwork Tea House/Ch.21.cbz', size: 11_000_000, chapters: ['21'] },
      ],
      upgradeCount: 14,
      alreadyMetCount: 0,
    },
    {
      fileName: 'Clockwork Tea House v03.pdf',
      size: 0,
      label: null,
      chapters: [],
      newChapters: [],
      replaces: [],
      upgradeCount: 0,
      alreadyMetCount: 0,
    },
  ],
  error: null,
  hasConflicts: true,
  newChapterCount: 2,
  replacedFileCount: 3,
  isUpgrade: false,
  suggestedSkips: [],
  ...over,
})

export const upgradeImportPlan = (queueItemId: number) =>
  importPlan(queueItemId, {
    isUpgrade: true,
    suggestedSkips: ['Clockwork Tea House v02.cbz'],
  })

const proposal: TorrentProposalDto = {
  id: 31,
  seriesId: 2,
  seriesTitle: title(2),
  title: 'The Ninth Kitchen v08-v10 [Digital] [Maki Scans]',
  indexer: 'Nyaa Mirror',
  sizeBytes: 612_000_000,
  span: { volumeStart: 8, volumeEnd: 10, chapters: [], wholeSeries: false },
  reasons: ['over_budget'],
  upgradeCount: 22,
  alreadyMetCount: 4,
  skippedCount: 0,
  missingCount: 0,
  unknownCount: 0,
  score: 14,
  tier: 'volume',
  status: 'pending',
  createdAtUtc: hoursAgo(20),
  resolvedAtUtc: null,
  queueItemId: null,
}
export const proposals: TorrentProposalDto[] = [
  proposal,
  {
    ...proposal,
    id: 32,
    seriesId: 8,
    seriesTitle: title(8),
    title: 'Bluefin Boys Complete Collection',
    span: { volumeStart: null, volumeEnd: null, chapters: [], wholeSeries: true },
    reasons: ['whole_series_pack'],
    sizeBytes: 4_300_000_000,
    upgradeCount: 0,
    missingCount: 210,
    tier: 'scanlator',
    createdAtUtc: daysAgo(2),
  },
]

export const upgradeSettings: UpgradeSettings = {
  enabled: true,
  defaultProfileId: 1,
  scanHour: 3,
  maxPerDay: 40,
  maxProbesPerRun: 200,
  quietPeriodDays: 7,
  trashRetentionDays: 14,
  scanIncognito: false,
  volumeSearch: true,
  torrentAutoGrabMaxBytes: 500_000_000,
  volumeMissingTolerance: 2,
  volumeSearchesPerRun: 10,
  proposalExpiryDays: 14,
}

export const upgradesSummary: UpgradesSummaryDto = {
  profilesConfigured: true,
  trashBytes: 1_480_000_000,
  trashFiles: 37,
  lastScanDate: '2026-10-19',
  scanRunning: false,
  pendingProposals: proposals.length,
  lastVolumeSearchDate: '2026-10-18',
}

const upgradeRow = (id: number, seriesId: number, chapter: number, over: Partial<UpgradeHistoryRowDto> = {}): UpgradeHistoryRowDto => ({
  id,
  seriesId,
  seriesTitle: title(seriesId),
  chapterId: 7000 + id,
  chapterNumber: chapter,
  chapterTitle: null,
  fileId: 8000 + id,
  fileName: `${title(seriesId)} - Ch.${chapter}.cbz`,
  before: snapshot({ score: 0 }),
  after: snapshot({ tier: 'scanlator', sourceName: 'asura', medianWidth: 1400, score: 11 }),
  profileName: 'Prefer sharp scans',
  trashBytes: 21_000_000,
  trashAvailable: true,
  createdAt: hoursAgo(5 + id),
  revertedAt: null,
  groupId: null,
  groupSize: 1,
  ...over,
})
export const upgradeHistoryRows: UpgradeHistoryRowDto[] = [
  upgradeRow(1, 1, 130),
  upgradeRow(2, 6, 188, { revertedAt: hoursAgo(1), trashAvailable: false }),
  upgradeRow(3, 11, 41, { trashAvailable: false }),
  ...[4, 5, 6].map((id, i) =>
    upgradeRow(id, 2, 90 + i, { groupId: 'vol-9', groupSize: 3, fileName: 'The Ninth Kitchen v09.cbz', createdAt: daysAgo(1) }),
  ),
]
export const upgradeHistoryPage = (rows = upgradeHistoryRows): UpgradeHistoryPageDto => ({
  rows,
  total: rows.length,
  page: 1,
  pageSize: 25,
})

const cutoffRow = (seriesId: number, chapter: number, fileId: number): CutoffUnmetRowDto => ({
  seriesId,
  seriesTitle: title(seriesId),
  chapterId: 6000 + fileId,
  chapterNumber: chapter,
  chapterTitle: null,
  fileId,
  fileName: `${title(seriesId)} - Ch.${chapter}.cbz`,
  quality: {
    fileId,
    tier: 'aggregator',
    group: null,
    pageCount: 22,
    medianWidth: 800,
    medianHeight: 1200,
    imageFormat: 'jpg',
    measured: true,
    score: 0,
    cutoffMet: false,
    trusted: false,
  },
  profileId: 1,
  profileName: 'Prefer sharp scans',
  cutoff: 'scanlator',
})
export const cutoffUnmetPage = (): CutoffUnmetPageDto => ({
  rows: [cutoffRow(1, 12, 1), cutoffRow(1, 13, 2), cutoffRow(6, 77, 3), cutoffRow(9, 4, 4), cutoffRow(11, 9, 5)],
  total: 5,
  page: 1,
  pageSize: 25,
})

const numberParam = (request: Request, name: string, fallback: number) =>
  Number(new URL(request.url).searchParams.get(name) ?? fallback)

export const queueHandlers = [
  http.get(api('/queue'), ({ request }) => {
    const seriesId = new URL(request.url).searchParams.get('seriesId')
    const items = seriesId ? queueItems.filter((q) => q.seriesId === Number(seriesId)) : queueItems
    return HttpResponse.json(queuePage(items))
  }),
  http.get(api('/queue/history'), ({ request }) => {
    const page = numberParam(request, 'page', 1)
    const pageSize = numberParam(request, 'pageSize', 25)
    return HttpResponse.json({
      items: historyItems.slice((page - 1) * pageSize, page * pageSize),
      total: historyItems.length,
      page,
      pageSize,
    } satisfies QueueHistoryDto)
  }),
  http.get(api('/queue/pause'), () => HttpResponse.json(noPause)),
  http.put(api('/queue/pause'), async ({ request }) => {
    const body = (await request.json()) as { source?: string; resumeAt?: string }
    const until = body.resumeAt ?? null
    return HttpResponse.json(
      body.source ? { all: null, sources: [{ sourceName: body.source, until }] } : { all: { until }, sources: [] },
    )
  }),
  http.delete(api('/queue/pause'), () => HttpResponse.json(noPause)),
  http.get(api('/queue/failed'), () => HttpResponse.json(failureGroups)),
  http.post(api('/queue/retry-failed'), () => HttpResponse.json({ affected: 6 })),
  http.post(api('/queue/remove-failed'), () => HttpResponse.json({ affected: 6 })),
  http.put(api('/queue/reorder'), () => new HttpResponse(null, { status: 204 })),
  http.delete(api('/queue'), () => HttpResponse.json({ cleared: queueItems.length })),
  http.get(api('/queue/:id/import-plan'), ({ params }) => {
    const id = Number(params.id)
    if (id === guardedItem.id) return HttpResponse.json(upgradeImportPlan(id))
    return HttpResponse.json(importPlan(id))
  }),
  http.post(api('/queue/:id/import'), () => HttpResponse.json({ imported: 3, linked: 20, skipped: 0, deleted: 3 })),
  http.post(api('/queue/:id/retry'), () => new HttpResponse(null, { status: 204 })),
  http.delete(api('/queue/:id'), () => new HttpResponse(null, { status: 204 })),
  http.get(api('/search/sources'), () => HttpResponse.json(sources)),
  http.get(api('/upgrades/summary'), () => HttpResponse.json(upgradesSummary)),
  http.get(api('/settings/upgrades'), () => HttpResponse.json(upgradeSettings)),
  http.get(api('/upgrades/proposals'), () => HttpResponse.json(proposals)),
  http.get(api('/upgrades/history'), () => HttpResponse.json(upgradeHistoryPage())),
  http.get(api('/upgrades/cutoff-unmet'), () => HttpResponse.json(cutoffUnmetPage())),
  http.post(api('/upgrades/scan'), () => HttpResponse.json({ started: true })),
  http.post(api('/upgrades/volume-search'), () => HttpResponse.json({ started: true })),
  http.post(api('/upgrades/history/:id/revert'), () => HttpResponse.json(upgradeHistoryRows[0])),
  http.post(api('/upgrades/history/group/:id/revert'), () => HttpResponse.json({})),
  http.post(api('/upgrades/proposals/:id/:action'), () => HttpResponse.json({ queueItemId: 999 })),
]
