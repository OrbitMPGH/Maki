import { http, HttpResponse } from 'msw'
import type { HttpHandler } from 'msw'
import type {
  ConnectionName,
  DiscoverSettings,
  DownloadClientKindInfo,
  DumpProgress,
  LibrarySettings,
  MetadataSettings,
  MonitoringSettings,
  RecommendationIndexStatus,
  SetupStatus,
  SourceLanguageSettings,
} from '../../../src/api/hooks'
import type { RootFolder } from '../../../src/api/types'
import { api, daysAgo } from '../api'
import { uiSettings } from '../shared'

export const idleDump: DumpProgress = {
  running: false,
  phase: 'idle',
  downloadedBytes: 0,
  totalBytes: null,
  bytesPerSecond: null,
  estimatedSecondsRemaining: null,
  startedAt: null,
  finishedAt: null,
  lastInstalled: false,
  lastError: null,
}

export const downloadingDump: DumpProgress = {
  running: true,
  phase: 'downloading',
  downloadedBytes: 217_400_000,
  totalBytes: 348_900_000,
  bytesPerSecond: 4_200_000,
  estimatedSecondsRemaining: 31,
  startedAt: daysAgo(0),
  finishedAt: null,
  lastInstalled: false,
  lastError: null,
}

export const readyIndex: RecommendationIndexStatus = {
  modelPresent: true,
  dumpPresent: true,
  vectorCount: 41_820,
  recommendableTotal: 41_820,
  running: false,
  phase: 'idle',
  embedded: 0,
  scanned: 0,
  startedAt: null,
  finishedAt: daysAgo(3),
  lastEmbedded: 0,
  lastError: null,
  estimatedSecondsRemaining: null,
  prebuiltEnabled: true,
  prebuiltInstalledAt: daysAgo(3),
  embeddingModel: 'base',
  useFullDump: false,
  modelSwitching: false,
  modelSwitchError: null,
}

export const rootFolderFixtures: RootFolder[] = [
  { id: 1, path: '/library/manga', freeSpace: 612_000_000_000, accessible: true },
  { id: 2, path: '/mnt/archive/manhwa', freeSpace: 48_000_000_000, accessible: true },
]

const downloadClientKinds: DownloadClientKindInfo[] = [
  { kind: 'qbittorrent', name: 'qBittorrent', usesUsername: true, defaultUrl: 'http://localhost:8080' },
  { kind: 'transmission', name: 'Transmission', usesUsername: true, defaultUrl: 'http://localhost:9091' },
  { kind: 'deluge', name: 'Deluge', usesUsername: false, defaultUrl: 'http://localhost:8112' },
]

const emptyConnection = (name: ConnectionName): Record<string, string | null> =>
  name === 'downloadclient'
    ? { kind: 'qbittorrent', url: null, username: null, password: null, label: null, pathMapFrom: null, pathMapTo: null }
    : name === 'indexer'
      ? { backend: 'prowlarr', url: null, apiKey: null }
      : name === 'kavita'
        ? { url: null, apiKey: null, pathMapFrom: null, pathMapTo: null }
        : { url: null }

export interface WizardOptions {
  /** Defaults to false, which is what makes the wizard open. */
  completed?: boolean
  folders?: RootFolder[]
  dump?: DumpProgress
  recommendations?: Partial<RecommendationIndexStatus>
  metadata?: Partial<MetadataSettings>
  connections?: Partial<Record<ConnectionName, Record<string, string | null>>>
  languages?: SourceLanguageSettings
  language?: string
}

/**
 * Every endpoint the first-run guide reads or saves, with writes that stick for the life of one
 * story run. Pass it through `msw.use` in `beforeEach` (not `parameters.msw`) so each run starts
 * from `options` again, and so these win over the settings area's defaults.
 */
export function wizardHandlers(options: WizardOptions = {}): HttpHandler[] {
  let setup: SetupStatus = { completed: options.completed ?? false }
  let folders = (options.folders ?? []).map((f) => ({ ...f }))
  let nextFolderId = 100
  let library: LibrarySettings = {
    writeComicInfo: true,
    folderNamingMode: 'rename',
    writeCoverToFolder: false,
    seriesFolderFormat: '{Series TitleYear}',
    chapterFormat: '{Series Title} - c{Chapter:000}',
    renameImportedFiles: true,
  }
  let discover: DiscoverSettings = { maxContentRating: 'erotica' }
  let monitoring: MonitoringSettings = { unmonitorSpecials: false, defaultMode: 'All' }
  let metadata: MetadataSettings = {
    useLocalDb: true,
    dumpPresent: false,
    dumpSizeBytes: null,
    dumpRefreshedAt: null,
    ...options.metadata,
  }
  let recommendations: RecommendationIndexStatus = { ...readyIndex, ...options.recommendations }
  let languages: SourceLanguageSettings = options.languages ?? {
    order: ['en', 'es', 'pt-br', 'fr', 'id'],
    disabled: ['fr', 'id'],
    available: ['en', 'es', 'pt-br', 'fr', 'id', 'ja'],
  }
  const connections: Record<ConnectionName, Record<string, string | null>> = {
    flaresolverr: { ...emptyConnection('flaresolverr'), ...options.connections?.flaresolverr },
    indexer: { ...emptyConnection('indexer'), ...options.connections?.indexer },
    downloadclient: { ...emptyConnection('downloadclient'), ...options.connections?.downloadclient },
    kavita: { ...emptyConnection('kavita'), ...options.connections?.kavita },
  }
  const ui = { ...uiSettings, language: options.language ?? '' }

  const connectionHandlers = (['flaresolverr', 'downloadclient', 'kavita'] as ConnectionName[]).flatMap((name) => [
    http.get(api(`/settings/${name}`), () => HttpResponse.json(connections[name])),
    http.put(api(`/settings/${name}`), async ({ request }) => {
      connections[name] = (await request.json()) as Record<string, string | null>
      return HttpResponse.json(connections[name])
    }),
    http.post(api(`/settings/${name}/test`), () => HttpResponse.json({ success: true })),
  ])

  return [
    http.get(api('/settings/setup'), () => HttpResponse.json(setup)),
    http.put(api('/settings/setup'), async ({ request }) => {
      setup = (await request.json()) as SetupStatus
      return HttpResponse.json(setup)
    }),
    http.get(api('/settings/ui'), () => HttpResponse.json(ui)),
    http.put(api('/settings/ui'), async ({ request }) => HttpResponse.json(await request.json())),

    http.get(api('/rootfolder'), () => HttpResponse.json(folders)),
    http.post(api('/rootfolder'), async ({ request }) => {
      const { path } = (await request.json()) as { path: string }
      const folder: RootFolder = { id: nextFolderId++, path, freeSpace: 220_000_000_000, accessible: true }
      folders = [...folders, folder]
      return HttpResponse.json(folder)
    }),
    http.delete(api('/rootfolder/:id'), ({ params }) => {
      folders = folders.filter((f) => f.id !== Number(params.id))
      return new HttpResponse(null, { status: 204 })
    }),

    http.get(api('/settings/library'), () => HttpResponse.json(library)),
    http.put(api('/settings/library'), async ({ request }) => {
      library = { ...library, ...((await request.json()) as LibrarySettings) }
      return HttpResponse.json(library)
    }),

    http.get(api('/settings/metadata'), () => HttpResponse.json(metadata)),
    http.put(api('/settings/metadata'), async ({ request }) => {
      metadata = { ...metadata, ...((await request.json()) as Partial<MetadataSettings>) }
      return HttpResponse.json(metadata)
    }),
    http.get(api('/settings/metadata/dump-progress'), () => HttpResponse.json(options.dump ?? idleDump)),

    http.get(api('/settings/recommendations'), () => HttpResponse.json(recommendations)),
    http.put(api('/settings/recommendations/model'), async ({ request }) => {
      const { model } = (await request.json()) as { model: string }
      recommendations = { ...recommendations, embeddingModel: model }
      return HttpResponse.json({ model, switching: false, reason: '' })
    }),
    http.post(api('/settings/recommendations/prebuilt/download'), () =>
      HttpResponse.json({ installed: false, reason: 'already current', rowCount: null }),
    ),

    http.get(api('/settings/discover'), () => HttpResponse.json(discover)),
    http.put(api('/settings/discover'), async ({ request }) => {
      discover = (await request.json()) as DiscoverSettings
      return HttpResponse.json(discover)
    }),

    http.get(api('/settings/monitoring'), () => HttpResponse.json(monitoring)),
    http.put(api('/settings/monitoring'), async ({ request }) => {
      monitoring = { ...monitoring, ...((await request.json()) as Partial<MonitoringSettings>) }
      return HttpResponse.json(monitoring)
    }),

    http.get(api('/settings/sources/languages'), () => HttpResponse.json(languages)),
    http.put(api('/settings/sources/languages'), async ({ request }) => {
      languages = (await request.json()) as SourceLanguageSettings
      return HttpResponse.json(languages)
    }),

    http.get(api('/settings/downloadclient/kinds'), () => HttpResponse.json(downloadClientKinds)),
    http.get(api('/settings/indexer'), () => HttpResponse.json(connections.indexer)),
    http.put(api('/settings/indexer'), async ({ request }) => {
      connections.indexer = (await request.json()) as Record<string, string | null>
      return HttpResponse.json(connections.indexer)
    }),
    http.post(api('/settings/indexer/test'), () => HttpResponse.json({ success: true })),
    ...connectionHandlers,
  ]
}
