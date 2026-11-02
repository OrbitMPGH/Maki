import { http, HttpResponse } from 'msw'
import type { ImageCacheInfo } from '../../../src/api/hooks'
import type {
  Analysis,
  FileDetail,
  HealthCheck,
  HealthFile,
  HealthFinding,
  HealthOperation,
  HealthOptions,
  HealthOverview,
  HealthScan,
  OperationDetail,
  OperationGroup,
  SourceFailures,
} from '../../../src/api/health'
import { api, cover, daysAgo, hoursAgo, minutesAgo } from '../api'

const check = (id: string, category: string, status: string, message: string, over: Partial<HealthCheck> = {}): HealthCheck => ({
  id,
  category,
  status,
  message,
  checkedAt: minutesAgo(8),
  acknowledged: false,
  ...over,
})

export const passingChecks: HealthCheck[] = [
  check('storage:root:1', 'storage', 'healthy', '/library/manga has 842 GB free (71%)'),
  check('storage:root:2', 'storage', 'healthy', '/library/manhwa has 311 GB free (44%)'),
  check('storage:backup', 'storage', 'healthy', 'Last backup was 2 days ago'),
  check('connections:metadata', 'connections', 'healthy', 'The metadata provider answered in 180 ms'),
  check('connections:downloadclient', 'connections', 'healthy', 'The download client is reachable'),
  check('system:database', 'system', 'healthy', 'The database passed its integrity check'),
  check('job:upgrades', 'job', 'ok', 'Upgrade scan ran today at 03:00'),
  check('downloads:queue', 'downloads', 'healthy', 'Nothing is stuck in the download queue'),
  check('library:roots', 'library', 'healthy', 'Every root folder is readable'),
]

export const warningChecks: HealthCheck[] = [
  check('storage:root:2', 'storage', 'warning', '/library/manhwa has 38 GB free (9%), below the 10% warning level'),
  check('storage:backup', 'storage', 'warning', 'The last backup was 11 days ago', { url: '/settings?tab=system' }),
  check('job:metadata', 'job', 'warning', 'The metadata refresh job has not finished in 3 days', { acknowledged: true }),
  check('connections:flaresolverr', 'connections', 'unavailable', 'FlareSolverr is not configured, 1 source needs it', {
    url: '/settings?tab=downloads',
  }),
]

export const errorChecks: HealthCheck[] = [
  check('storage:root:1', 'storage', 'error', '/library/manga is not readable. Check that its drive or share is mounted.'),
  check('system:database', 'system', 'error', 'The database reported a failed integrity check'),
  check('connections:downloadclient', 'connections', 'error', 'The download client refused the connection'),
  ...warningChecks.slice(0, 2),
]

export const groupedChecks: HealthCheck[] = [
  check('legacy:source:comick', 'downloads', 'warning', 'Comick is failing to refresh 4 series'),
  ...['Bluefin Boys', 'Snowfield Letters', 'The Ninth Kitchen', 'Clockwork Tea House'].map((name, i) =>
    check(`legacy:series:${i + 1}`, 'library', 'warning', `${name} is monitored but has no enabled sources`),
  ),
  check('legacy:mapping:1', 'library', 'warning', 'Lanterns Over Kasumi is failing to refresh from Comick', { acknowledged: true }),
  check('legacy:mapping:2', 'library', 'warning', 'Neon Shrine Maiden is failing to refresh from Comick'),
  check('legacy:mapping:3', 'library', 'warning', 'Under the Willow Gate is failing to refresh from Comick'),
  check('storage:root:1', 'storage', 'healthy', '/library/manga has 842 GB free (71%)'),
]

export const runningScan: HealthScan = { id: 7, status: 'running', completed: 1840, total: 6200, verify: false }
export const verifyScan: HealthScan = { id: 8, status: 'pending', completed: 0, total: 6200, verify: true }

export const overview = (over: Partial<HealthOverview> = {}): HealthOverview => ({
  checks: passingChecks,
  openFindings: 0,
  files: 6200,
  scans: [{ id: 6, status: 'completed', completed: 6200, total: 6200, verify: false }],
  roots: [
    { id: 1, path: '/library/manga' },
    { id: 2, path: '/library/manhwa' },
  ],
  ...over,
})

const finding = (id: number, fileId: number, kind: string, severity: string, message: string, state = 'open'): HealthFinding => ({
  id,
  fileId,
  version: `v${id}`,
  kind,
  severity,
  message,
  state,
})

const file = (id: number, relativePath: string, over: Partial<HealthFile> = {}): HealthFile => ({
  id,
  relativePath,
  version: `fv${id}`,
  rootFolderId: 1,
  seriesId: 1,
  chapterFileId: 3000 + id,
  size: 31_000_000,
  contentHash: `9f2c1ab07d${id}e44b8a6103c5d7e29f01a`,
  status: 'complete',
  analyzedAt: hoursAgo(6 + id),
  findings: [],
  ...over,
})

export const healthFiles: HealthFile[] = [
  file(1, 'Lanterns Over Kasumi/Lanterns Over Kasumi - Ch.57.cbz', {
    status: 'corrupt',
    findings: [finding(101, 1, 'corrupt', 'error', 'Page 12 failed its CRC check'), finding(102, 1, 'damagedImage', 'warning', 'Page 12 cannot be decoded')],
  }),
  file(2, 'Starlit Ferry/Starlit Ferry - Ch.4.cbz', {
    seriesId: undefined,
    chapterFileId: undefined,
    status: 'partial',
    findings: [finding(103, 2, 'unlinked', 'warning', 'This archive backs no chapter')],
  }),
  file(3, 'Clockwork Tea House/Clockwork Tea House - Ch.18.cbz', {
    seriesId: 7,
    chapterFileId: undefined,
    findings: [finding(104, 3, 'unlinked', 'warning', 'This archive backs no chapter'), finding(105, 3, 'duplicate', 'warning', 'Same chapter as another file')],
  }),
  file(4, 'The Ninth Kitchen/The Ninth Kitchen - Ch.61.cbz', {
    seriesId: 2,
    size: -1,
    status: 'failed',
    analyzedAt: undefined,
    findings: [finding(106, 4, 'missing', 'error', 'The file is no longer on disk')],
  }),
  file(5, 'Neon Shrine Maiden/Neon Shrine Maiden - Ch.12.cbz', {
    seriesId: 11,
    size: 0,
    findings: [finding(107, 5, 'empty', 'warning', 'The archive is empty', 'acknowledged')],
  }),
  file(6, 'Bluefin Boys/Bluefin Boys - Ch.1.cbz', { seriesId: 8, findings: [] }),
]

const analysis = (status: string, verified: boolean, pages = 18, over: Partial<Analysis> = {}): Analysis => ({
  status,
  hash: verified ? 'ab12cd34' : undefined,
  verified,
  pages: Array.from({ length: pages }, (_, i) => ({ name: `${String(i + 1).padStart(3, '0')}.jpg`, width: 969, height: 1400 })),
  problems: [],
  ...over,
})

export const fileDetails: Record<number, FileDetail> = {
  1: {
    file: healthFiles[0],
    analysis: analysis('corrupt', true, 22, {
      problems: [{ kind: 'corrupt', severity: 'error', message: 'Page 12 failed its CRC check' }],
    }),
    findings: healthFiles[0].findings,
    chapters: [{ id: 5701, number: 57, title: 'The Lamp Keeper', wanted: true }],
    mappings: [
      { id: 1, sourceName: 'MangaDex', priority: 1 },
      { id: 2, sourceName: 'Comick', priority: 2 },
    ],
    match: null,
  },
  2: {
    file: healthFiles[1],
    analysis: analysis('partial', false, 20, {
      problems: [{ kind: 'incomplete', severity: 'info', message: 'Indexed only: page contents were not read.' }],
    }),
    findings: healthFiles[1].findings,
    chapters: [],
    mappings: [],
    match: {
      recognized: true,
      labelKind: 'chapter',
      number: 4,
      seriesId: 13,
      seriesTitle: 'Starlit Ferry',
      chapters: [{ id: 1304, number: 4, title: 'Low Tide', hasFile: false }],
      counterparts: [],
    },
  },
  3: {
    file: healthFiles[2],
    analysis: analysis('complete', true, 30),
    findings: healthFiles[2].findings,
    chapters: [],
    mappings: [{ id: 3, sourceName: 'Comick', priority: 1 }],
    match: {
      recognized: true,
      labelKind: 'chapter',
      number: 18,
      seriesId: 7,
      seriesTitle: 'Clockwork Tea House',
      chapters: [{ id: 718, number: 18, title: 'Steam and Silence', hasFile: true }],
      counterparts: [
        {
          chapterFileId: 3318,
          relativePath: 'Clockwork Tea House/Clockwork Tea House - Ch.18 [Comick].cbz',
          size: 28_400_000,
          sourceName: 'Comick',
          healthFileId: 9,
          version: 'fv9',
          status: 'complete',
          pages: 28,
          pixelHeight: 39_200,
          contentHash: '55aa0e12fbd34c81',
          chapterIds: [718],
        },
      ],
    },
  },
  4: {
    file: healthFiles[3],
    analysis: analysis('failed', false, 0),
    findings: healthFiles[3].findings,
    chapters: [
      { id: 6101, number: 61, title: 'The Ninth Course', wanted: true },
      { id: 6102, number: 62, wanted: false },
    ],
    mappings: [{ id: 4, sourceName: 'MangaDex', priority: 1 }],
    match: null,
  },
}

export const unlinkedNoSeries: FileDetail = {
  ...fileDetails[2],
  match: { recognized: true, labelKind: 'volumes', number: 1, volumeEnd: 3, chapters: [], counterparts: [] },
}
export const unlinkedUnrecognized: FileDetail = {
  ...fileDetails[2],
  match: { recognized: false, labelKind: 'unrecognized', chapters: [], counterparts: [] },
}

const operation = (id: number, fileId: number, kind: string, status: string, over: Partial<HealthOperation> = {}): HealthOperation => {
  const target = healthFiles.find((f) => f.id === fileId)
  const seriesTitle = target?.seriesId ? target.relativePath.split('/')[0] : null
  return {
    id,
    fileId,
    kind,
    status,
    version: `ov${id}`,
    createdAt: hoursAgo(id * 2),
    relativePath: target?.relativePath ?? null,
    seriesId: target?.seriesId ?? null,
    seriesTitle,
    coverUrl: target?.seriesId ? cover(target.seriesId) : null,
    ...over,
  }
}

export const operations: HealthOperation[] = [
  operation(1, 1, 'repair', 'review'),
  operation(2, 4, 'delete', 'review'),
  operation(3, 3, 'repair', 'running'),
  operation(4, 5, 'delete', 'completed'),
  operation(5, 2, 'repair', 'failed', { error: 'No source had a candidate for chapter 4' }),
]

const failedDeletionSeries = ['Lanterns Over Kasumi', 'The Ninth Kitchen', 'Snowfield Letters', 'Ashen Crown Regression', 'Iron Petal Academy']

/** A bulk delete that the filesystem refused a hundred times, plus two replacements waiting for review. */
export const manyFailedDeletions: HealthOperation[] = [
  operation(102, 1, 'repair', 'review'),
  operation(101, 3, 'repair', 'review'),
  ...Array.from({ length: 100 }, (_, i) => {
    const title = failedDeletionSeries[i % failedDeletionSeries.length]
    const seriesId = (i % failedDeletionSeries.length) + 1
    return operation(100 - i, 200 + i, 'delete', 'failed', {
      error: 'File deletion failed; file links retained',
      relativePath: `${title}/${title} - Ch.${40 + i}.cbz`,
      seriesId,
      seriesTitle: title,
      coverUrl: cover(seriesId),
      createdAt: hoursAgo(3),
    })
  }),
]

/** What HealthController.Operations does: one group per kind, status and error, newest first, three rows each. */
export const groupOperations = (items: HealthOperation[]): { items: OperationGroup[]; total: number } => {
  const groups = new Map<string, HealthOperation[]>()
  for (const op of [...items].sort((a, b) => b.id - a.id)) {
    const key = `${op.kind}|${op.status}|${op.error ?? ''}`
    groups.set(key, [...(groups.get(key) ?? []), op])
  }
  const list = [...groups].map(([key, rows]) => ({
    key,
    kind: rows[0].kind,
    status: rows[0].status,
    error: rows[0].error ?? null,
    count: rows.length,
    rows: rows.slice(0, 3),
  }))
  return { items: list, total: list.length }
}

/** The rows behind one group key, as `/operations/group` serves them. */
export const groupRows = (items: HealthOperation[], key: string, take: number) => {
  const rows = items.filter((op) => `${op.kind}|${op.status}|${op.error ?? ''}` === key).sort((a, b) => b.id - a.id)
  return { items: rows.slice(0, take), total: rows.length }
}

export const operationDetails: Record<number, OperationDetail> = {
  1: {
    operation: operations[0],
    file: healthFiles[0],
    chapters: [{ id: 5701, title: 'The Lamp Keeper', wanted: true }],
    candidates: [
      {
        chapterId: 5701,
        analysis: analysis('complete', true, 22),
      },
    ],
    requiresReset: true,
    retryable: false,
  },
  2: {
    operation: operations[1],
    file: healthFiles[3],
    chapters: [{ id: 6101, title: 'The Ninth Course', wanted: true }],
    candidates: [],
    requiresReset: false,
    retryable: false,
  },
  3: {
    operation: operations[2],
    file: healthFiles[2],
    chapters: [{ id: 718, title: 'Steam and Silence', wanted: false }],
    candidates: [],
    requiresReset: false,
    retryable: false,
  },
}
export const healthyDeleteDetail: OperationDetail = {
  operation: operations[1],
  file: healthFiles[0],
  chapters: [{ id: 5701, title: 'The Lamp Keeper', wanted: false }],
  candidates: [],
  requiresReset: false,
  retryable: false,
}

export const historyEntries = [
  { id: 1, createdAt: minutesAgo(8), kind: 'scan', message: 'Index scan 6 finished: 6200 files, 3 new findings' },
  { id: 2, createdAt: hoursAgo(3), kind: 'transition', message: 'Disk space on /library/manhwa went from OK to Warning' },
  { id: 3, createdAt: hoursAgo(9), kind: 'repair', message: 'Replacement for Lanterns Over Kasumi - Ch.57.cbz is ready for review' },
  { id: 4, createdAt: daysAgo(1), kind: 'delete', message: 'Removed the record for Neon Shrine Maiden - Ch.12.cbz' },
  { id: 5, createdAt: daysAgo(2), kind: 'job', message: 'Upgrade scan ran and queued 3 chapters' },
]

export const healthOptions: HealthOptions = {
  warningPercent: 10,
  errorPercent: 3,
  warningGiB: 50,
  errorGiB: 10,
  backupDays: 7,
  timeZone: null,
  scanHour: 3,
  automaticScanning: true,
  scanWorkers: 0,
}

export const sourceFailures: SourceFailures = {
  series: [
    { id: 1, seriesId: 11, title: 'Neon Shrine Maiden', error: 'HTTP 403 while loading the chapter list', lastRefresh: hoursAgo(6) },
    { id: 2, seriesId: 12, title: 'Under the Willow Gate', error: 'The source answered with an error', lastRefresh: hoursAgo(9) },
    { id: 3, seriesId: 7, title: 'Clockwork Tea House', error: 'Could not reach the source' },
  ],
  refreshing: false,
  done: 0,
  total: 0,
}

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
    finishedAt: daysAgo(1),
    lastError: null,
  },
  usage: {
    coverFiles: 241,
    coverBytes: 98_000_000,
    thumbnailFiles: 5400,
    thumbnailBytes: 412_000_000,
    seriesTotal: 245,
    coversMissing: 4,
  },
}

/** Page images are served per archive; the shared generated page stands in for them. */
const pageRedirect = (seed: string, n: string) => HttpResponse.redirect(`/mock/page/${encodeURIComponent(seed)}/${n}.svg`)

export const healthHandlers = [
  http.get(api('/health'), () => HttpResponse.json(overview())),
  http.get(api('/health/files'), ({ request }) => {
    const params = new URL(request.url).searchParams
    const search = (params.get('search') ?? '').toLowerCase()
    const state = params.get('state')
    const kind = params.get('kind')
    const items = healthFiles.filter(
      (f) =>
        f.relativePath.toLowerCase().includes(search) &&
        (!kind || f.findings.some((x) => x.kind === kind)) &&
        (!state || f.findings.length === 0 || f.findings.some((x) => x.state === state)),
    )
    return HttpResponse.json({ items, total: items.length })
  }),
  http.get(api('/health/files/:id'), ({ params }) =>
    HttpResponse.json(fileDetails[Number(params.id)] ?? fileDetails[1]),
  ),
  http.get(api('/health/files/:id/pages/:n'), ({ params }) => pageRedirect(`file-${params.id}`, String(params.n))),
  http.get(api('/health/operations'), () => HttpResponse.json(groupOperations(operations))),
  http.get(api('/health/operations/group'), ({ request }) => {
    const url = new URL(request.url)
    return HttpResponse.json(groupRows(operations, url.searchParams.get('key') ?? '', Number(url.searchParams.get('take') ?? 30)))
  }),
  http.get(api('/health/operations/:id'), ({ params }) =>
    HttpResponse.json(operationDetails[Number(params.id)] ?? operationDetails[1]),
  ),
  http.get(api('/health/operations/:id/candidates/:chapterId/pages/:n'), ({ params }) =>
    pageRedirect(`candidate-${params.chapterId}`, String(params.n)),
  ),
  http.get(api('/health/history'), () => HttpResponse.json({ items: historyEntries, total: historyEntries.length })),
  http.get(api('/health/options'), () => HttpResponse.json(healthOptions)),
  http.put(api('/health/options'), () => HttpResponse.json(operations[0])),
  http.get(api('/health/sources/:source'), () => HttpResponse.json(sourceFailures)),
  http.post(api('/health/sources/:source/refresh'), () => HttpResponse.json(operations[0])),
  http.post(api('/health/refresh'), () => HttpResponse.json(operations[0])),
  http.post(api('/health/scans'), () => HttpResponse.json(operations[0])),
  http.post(api('/health/scans/:id/cancel'), () => HttpResponse.json(operations[0])),
  http.post(api('/health/checks/acknowledge'), () => HttpResponse.json(operations[0])),
  http.post(api('/health/checks/acknowledge/bulk'), () => HttpResponse.json(operations[0])),
  http.post(api('/health/findings/review'), () => HttpResponse.json(operations[0])),
  http.put(api('/health/findings/:id'), () => HttpResponse.json(operations[0])),
  http.post(api('/health/imports'), () => HttpResponse.json(operations[0])),
  http.post(api('/health/repairs'), () => HttpResponse.json(operations[0])),
  http.post(api('/health/deletions/preview'), () => HttpResponse.json(operations[1])),
  http.post(api('/health/deletions/bulk'), async ({ request }) => {
    const body = (await request.json()) as { files: { fileId: number }[] }
    return HttpResponse.json({
      deleted: Math.max(0, body.files.length - 1),
      failures: [{ id: body.files[0]?.fileId ?? 1, relativePath: healthFiles[0].relativePath, message: 'The file is open in another process' }],
    })
  }),
  http.post(api('/health/operations/:id/apply'), () => HttpResponse.json(operations[0])),
  http.post(api('/health/operations/:id/cancel'), () => HttpResponse.json(operations[0])),
  http.get(api('/system/image-cache'), () => HttpResponse.json(imageCache)),
  http.post(api('/system/image-cache/rebuild'), () => HttpResponse.json({ started: true })),
]
