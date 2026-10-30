import { http, HttpResponse, type HttpHandler } from 'msw'
import { api } from './api'
import * as d from './settings/data'
import { pageConversionHandlers } from './settings/pageConversion'
import { browseFoldersHandler } from './settings/folders'
import { healthOptions } from './operations/health'

/** A PUT whose reply is the body it was sent, which is how most settings records answer. */
const putEcho = (path: string) =>
  http.put(api(path), async ({ request }) => HttpResponse.json(await request.json()))
const ok = () => new HttpResponse(null, { status: 204 })
const success = () => HttpResponse.json({ success: true })

export const settingsHandlers: HttpHandler[] = [
  // System: the Library health card reads the Health page's own options endpoint.
  http.get(api('/health/options'), () => HttpResponse.json(healthOptions)),
  http.put(api('/health/options'), async ({ request }) => HttpResponse.json(await request.json())),
  // Account
  http.get(api('/account/2fa'), () => HttpResponse.json(d.twoFactorOff)),
  http.get(api('/account/apikeys'), () => HttpResponse.json(d.apiKeys)),
  http.post(api('/account/apikeys'), async ({ request }) => {
    const body = (await request.json()) as { name: string; scope?: 'Full' | 'Opds' }
    return HttpResponse.json({
      key: { id: 4, name: body.name, prefix: 'mk_9z8y', scope: body.scope ?? 'Full', createdAt: new Date().toISOString(), lastUsedAt: null, revokedAt: null },
      secret: 'mk_9z8y_example-secret-shown-once',
    })
  }),
  http.delete(api('/account/apikeys/:id'), ok),
  http.post(api('/account/password'), ok),
  http.post(api('/account/2fa/setup'), () =>
    HttpResponse.json({ sharedKey: 'JBSW Y3DP EHPK 3PXP', authenticatorUri: 'otpauth://totp/Maki:maki?secret=JBSWY3DPEHPK3PXP&issuer=Maki' }),
  ),
  http.post(api('/account/2fa/enable'), () => HttpResponse.json({ recoveryCodes: ['a1b2-c3d4', 'e5f6-g7h8', 'i9j0-k1l2', 'm3n4-o5p6'] })),
  http.post(api('/account/2fa/recovery-codes'), () => HttpResponse.json({ recoveryCodes: ['q1w2-e3r4', 't5y6-u7i8'] })),
  http.post(api('/account/2fa/disable'), ok),
  http.post(api('/account/sessions/revoke-all'), ok),
  http.delete(api('/account/oidc'), ok),
  http.post(api('/auth/oidc/link'), ok),

  // Users and security
  http.get(api('/users'), () => HttpResponse.json(d.users)),
  http.get(api('/users/auditlog'), () => HttpResponse.json(d.authEvents)),
  http.post(api('/users'), async ({ request }) => HttpResponse.json({ ...d.users[1], id: 9, ...((await request.json()) as object) })),
  http.put(api('/users/:id'), async ({ request, params }) =>
    HttpResponse.json({ ...d.users[1], id: Number(params.id), ...((await request.json()) as object) }),
  ),
  http.delete(api('/users/:id'), ok),
  http.post(api('/users/:id/2fa/reset'), ok),
  http.delete(api('/users/:id/oidc'), ok),
  http.get(api('/settings/security'), () => HttpResponse.json(d.securitySettings)),
  putEcho('/settings/security'),
  http.get(api('/settings/signin'), () => HttpResponse.json({ coverMosaic: false })),
  http.get(api('/settings/usermode'), () => HttpResponse.json({ singleUser: false, available: false })),
  http.put(api('/settings/usermode'), async ({ request }) => {
    const { singleUser } = (await request.json()) as { singleUser: boolean }
    return HttpResponse.json({ singleUser, available: true })
  }),
  putEcho('/settings/signin'),
  http.get(api('/settings/oidc'), () => HttpResponse.json(d.oidcSettings)),
  putEcho('/settings/oidc'),

  // Preferences
  http.get(api('/settings/discover'), () => HttpResponse.json(d.discoverSettings)),
  putEcho('/settings/discover'),
  http.put(api('/settings/ui'), async ({ request }) => HttpResponse.json(await request.json())),
  http.get(api('/inbox/prefs'), () => HttpResponse.json(d.inboxPrefs)),
  putEcho('/inbox/prefs'),
  http.get(api('/push/public-key'), () => HttpResponse.json({ publicKey: 'BEl62iUYgUivxIkv69yViEuiBIa-Ib9-SkvMeAtA3LFgDzkrxZJjSgSnfckjBJuBkr3qBUYIHBQFLXYp5Nksh8U' })),
  http.get(api('/push/prefs'), () => HttpResponse.json(d.pushPrefs)),
  putEcho('/push/prefs'),
  http.post(api('/push/subscription'), () => HttpResponse.json({ subscribed: true })),
  http.delete(api('/push/subscription'), ok),
  http.post(api('/push/test'), () => HttpResponse.json({ sent: 1, failed: 0 })),

  // Reading
  http.get(api('/reader/used'), () => HttpResponse.json({ used: true, kavita: true })),
  http.get(api('/settings/reader'), () => HttpResponse.json(d.readerSettings)),
  putEcho('/settings/reader'),
  http.get(api('/settings/rating-prompts'), () => HttpResponse.json({ enabled: true })),
  putEcho('/settings/rating-prompts'),
  http.get(api('/readingprofiles'), () => HttpResponse.json(d.readingProfiles)),
  http.post(api('/readingprofiles'), async ({ request }) => HttpResponse.json({ id: 10, ...((await request.json()) as object) })),
  http.put(api('/readingprofiles/:id'), async ({ request, params }) =>
    HttpResponse.json({ id: Number(params.id), ...((await request.json()) as object) }),
  ),
  http.delete(api('/readingprofiles/:id'), ok),
  http.get(api('/progress/settings'), () => HttpResponse.json(d.progressSettings)),
  putEcho('/progress/settings'),
  http.get(api('/progress/summary'), () => HttpResponse.json(d.progressSummary)),
  http.put(api('/progress/goals'), () => HttpResponse.json(d.progressSummary.goals)),
  http.delete(api('/progress/goals/:id'), ok),
  http.get(api('/settings/opds'), () => HttpResponse.json(d.opdsOn)),
  http.put(api('/settings/opds'), async ({ request }) => {
    const body = (await request.json()) as { enabled: boolean; trackProgress: boolean }
    return HttpResponse.json({ ...d.opdsOn, ...body, feedUrl: body.enabled ? '/api/v1/opds/a91f3c8e5d2b47f0b6c1' : null })
  }),
  http.post(api('/settings/opds/token'), () => HttpResponse.json({ ...d.opdsOn, tokenPrefix: 'f07be2', feedUrl: '/api/v1/opds/f07be2c19a44d85e7a30' })),
  http.get(api('/reader/import/kavita'), () =>
    HttpResponse.json({
      running: false,
      finishedAt: '2026-10-18T20:00:00Z',
      result: { seriesMatched: 38, chaptersMarked: 912, seriesUnmatched: 3, seriesFailed: 0, failedTitles: [] },
      error: null,
    }),
  ),
  http.post(api('/reader/import/kavita'), () => HttpResponse.json({})),

  // Library
  browseFoldersHandler,
  http.get(api('/rootfolder'), () => HttpResponse.json(d.rootFolders)),
  http.post(api('/rootfolder'), async ({ request }) => {
    const { path } = (await request.json()) as { path: string }
    return HttpResponse.json({ id: 9, path, freeSpace: 500 * 1024 ** 3, accessible: true })
  }),
  http.delete(api('/rootfolder/:id'), ok),
  http.get(api('/settings/library'), () => HttpResponse.json(d.libraryDefaults)),
  putEcho('/settings/library'),
  http.get(api('/settings/monitoring'), () => HttpResponse.json(d.monitoring)),
  putEcho('/settings/monitoring'),
  http.get(api('/settings/nameparsing'), () => HttpResponse.json(d.nameParsing)),
  http.put(api('/settings/nameparsing'), async ({ request }) => {
    const { languages } = (await request.json()) as { languages: string[] }
    return HttpResponse.json({ ...d.nameParsing, languages })
  }),
  http.get(api('/settings/naming/tokens'), () => HttpResponse.json(d.namingTokens)),
  http.post(api('/settings/naming/preview'), async ({ request }) => {
    const body = (await request.json()) as { seriesFolderFormat: string; chapterFormat: string }
    const bad = (s: string) => (s.includes('{') && !s.includes('}') ? ['The token is not closed.'] : [])
    return HttpResponse.json({
      seriesFolder: body.seriesFolderFormat.replace('{Series TitleYear}', 'Lanterns Over Kasumi (2021)').replace('{Series Title}', 'Lanterns Over Kasumi'),
      chapterFile: body.chapterFormat.replace('{Series Title}', 'Lanterns Over Kasumi').replace('{Chapter:000}', '012'),
      seriesFolderErrors: bad(body.seriesFolderFormat),
      chapterErrors: bad(body.chapterFormat),
    })
  }),
  http.post(api('/series/rename'), () => HttpResponse.json([])),
  http.get(api('/settings/recommendations'), () => HttpResponse.json(d.recommendationIndex)),
  http.post(api('/settings/recommendations/model'), () => HttpResponse.json({ model: 'base', switching: false, reason: 'Already using base.' })),
  http.post(api('/settings/recommendations/build'), () => HttpResponse.json({ started: true })),
  http.get(api('/settings/metadata/dump-progress'), () => HttpResponse.json(d.dumpProgressIdle)),
  http.post(api('/settings/metadata/refresh'), () => HttpResponse.json({ started: true, alreadyRunning: false })),
  ...pageConversionHandlers,
  http.get(api('/pageconversion'), () => HttpResponse.json(d.pageConversionIdle)),
  http.put(api('/pageconversion/settings'), async ({ request }) =>
    HttpResponse.json({ ...d.pageConversionIdle, settings: await request.json() }),
  ),
  http.put(api('/pageconversion/parallel'), async ({ request }) => {
    const { limit } = (await request.json()) as { limit: number | null }
    return HttpResponse.json({ ...d.pageConversionIdle, parallel: { ...d.pageConversionIdle.parallel, limit } })
  }),
  http.post(api('/pageconversion/start'), () => HttpResponse.json(d.pageConversionRunning)),
  http.post(api('/pageconversion/pause'), () =>
    HttpResponse.json({ ...d.pageConversionRunning, pass: { ...d.pageConversionRunning.pass, state: 'paused', currentFile: null } }),
  ),
  http.get(api('/upscaling'), () => HttpResponse.json(d.upscalingOn)),
  http.put(api('/upscaling/settings'), async ({ request }) =>
    HttpResponse.json({ ...d.upscalingOn, settings: await request.json() }),
  ),
  http.post(api('/upscaling/models/:id/download'), () => HttpResponse.json(d.upscalingOn)),
  http.delete(api('/upscaling/models/:id'), () => HttpResponse.json(d.upscalingOn)),
  http.post(api('/upscaling/cache/clear'), () => HttpResponse.json({ ...d.upscalingOn, cache: { bytes: 0, files: 0 } })),
  http.post(api('/upscaling/benchmark'), () => HttpResponse.json({ seconds: 5.5 })),
  http.get(api('/recyclebin'), () => HttpResponse.json(d.recycleBin)),
  http.get(api('/recyclebin/retention'), () => HttpResponse.json({ days: 14 })),
  http.put(api('/recyclebin/retention'), ok),
  http.post(api('/recyclebin/:id/restore'), () => HttpResponse.json({ linked: true, chapters: 1 })),
  http.delete(api('/recyclebin/:id'), ok),
  http.delete(api('/recyclebin'), () => HttpResponse.json({ deleted: 3, failed: 0 })),

  // Downloads
  http.get(api('/settings/download'), () => HttpResponse.json(d.downloadSettings)),
  putEcho('/settings/download'),
  http.get(api('/settings/readcleanup'), () => HttpResponse.json(d.readFileCleanup)),
  putEcho('/settings/readcleanup'),
  http.get(api('/settings/sources/priority'), () => HttpResponse.json(d.sourcePriority)),
  putEcho('/settings/sources/priority'),
  http.get(api('/settings/sources/languages'), () => HttpResponse.json(d.sourceLanguages)),
  putEcho('/settings/sources/languages'),
  http.get(api('/search/sources'), () => HttpResponse.json(d.sources)),
  http.get(api('/custom-sources'), () => HttpResponse.json([])),
  http.get(api('/custom-sources/repositories'), () => HttpResponse.json(d.customRepositories)),
  http.post(api('/custom-sources/repositories'), () => HttpResponse.json(d.customRepositories[0])),
  http.delete(api('/custom-sources/repositories'), () => HttpResponse.json({ detached: 2 })),
  putEcho('/custom-sources/repositories/autoupdate'),
  http.post(api('/custom-sources/repositories/refresh'), () => HttpResponse.json(d.customRepositories)),
  http.post(api('/custom-sources/repositories/install'), () => HttpResponse.json({ created: true, sourceName: 'custom-lantern' })),
  http.get(api('/custom-sources/:id/update'), () => HttpResponse.json(d.customSourceUpdatePreview)),
  http.post(api('/custom-sources/:id/update'), () => HttpResponse.json({ created: false, sourceName: 'custom-quietpen' })),
  http.post(api('/custom-sources/:id/revert'), () => HttpResponse.json({ created: false, sourceName: 'custom-quietpen' })),
  http.post(api('/custom-sources/test'), () => HttpResponse.json(d.customSourceTestReport)),
  http.post(api('/custom-sources/probe'), () => HttpResponse.json(d.customSourceProbe)),
  http.post(api('/custom-sources/probe/search'), () => HttpResponse.json(d.customSourceSearchProbe)),
  http.get(api('/custom-sources/:id'), () => HttpResponse.json(d.customSourcesList[0].definition)),
  http.put(api('/custom-sources/:id'), () => HttpResponse.json({ created: true, sourceName: 'custom-quietpen' })),
  http.delete(api('/custom-sources/:id'), ok),
  http.get(api('/settings/flaresolverr'), () => HttpResponse.json(d.flaresolverr)),
  putEcho('/settings/flaresolverr'),
  http.post(api('/settings/flaresolverr/test'), success),
  http.get(api('/settings/downloadclient/kinds'), () => HttpResponse.json(d.downloadClientKinds)),
  http.get(api('/settings/downloadclient'), () => HttpResponse.json(d.downloadClient)),
  putEcho('/settings/downloadclient'),
  http.post(api('/settings/downloadclient/test'), success),
  http.get(api('/settings/indexer'), ({ request }) => {
    const backend = new URL(request.url).searchParams.get('backend')
    return HttpResponse.json(backend === 'jackett' ? d.indexerJackett : d.indexerProwlarr)
  }),
  putEcho('/settings/indexer'),
  http.post(api('/settings/indexer/test'), success),
  http.get(api('/settings/indexer/options'), () => HttpResponse.json(d.indexerOptions)),
  putEcho('/settings/indexer/options'),
  http.get(api('/settings/indexer/indexers'), () => HttpResponse.json(d.indexerList)),
  http.get(api('/settings/upgrades'), () => HttpResponse.json(d.upgradeSettings)),
  putEcho('/settings/upgrades'),
  http.post(api('/upgrades/scan'), () => HttpResponse.json({ started: true })),
  http.get(api('/upgrade-profiles'), () => HttpResponse.json(d.upgradeProfiles)),
  http.post(api('/upgrade-profiles'), async ({ request }) =>
    HttpResponse.json({ id: 9, version: 1, seriesCount: 0, ...((await request.json()) as object) }),
  ),
  http.put(api('/upgrade-profiles/:id'), async ({ request, params }) =>
    HttpResponse.json({ id: Number(params.id), version: 2, seriesCount: 0, ...((await request.json()) as object) }),
  ),
  http.delete(api('/upgrade-profiles/:id'), ok),
  http.get(api('/quality-formats'), () => HttpResponse.json(d.qualityFormats)),
  http.post(api('/quality-formats'), async ({ request }) =>
    HttpResponse.json({ id: 9, version: 1, profileCount: 0, ...((await request.json()) as object) }),
  ),
  http.put(api('/quality-formats/:id'), async ({ request, params }) =>
    HttpResponse.json({ id: Number(params.id), version: 2, profileCount: 0, ...((await request.json()) as object) }),
  ),
  http.delete(api('/quality-formats/:id'), ok),

  // Integrations
  http.get(api('/settings/kavita'), () => HttpResponse.json(d.kavitaConnection)),
  putEcho('/settings/kavita'),
  http.put(api('/settings/kavita/user'), async ({ request }) => HttpResponse.json(await request.json())),
  http.post(api('/settings/kavita/test'), success),
  http.get(api('/settings/kavita/libraries'), () => HttpResponse.json(d.kavitaLibraries)),
  http.get(api('/settings/suwayomi'), () => HttpResponse.json(d.suwayomiConnection)),
  http.put(api('/settings/suwayomi'), async ({ request }) => {
    const body = (await request.json()) as { url: string | null; authMode: string; username: string | null; password: string | null }
    return HttpResponse.json({ url: body.url, authMode: body.authMode, username: body.username, passwordSet: body.password !== null })
  }),
  http.post(api('/settings/suwayomi/test'), () => HttpResponse.json({ success: true, version: 'v2.1.1', buildType: 'Stable', sourceCount: 12 })),
  http.post(api('/settings/suwayomi/sources/refresh'), () => HttpResponse.json({ reachable: true, error: null, installed: 3 })),
  http.put(api('/settings/suwayomi/sources/:id'), ok),
  http.get(api('/settings/scrobble'), () => HttpResponse.json(d.scrobbleSettings)),
  putEcho('/settings/scrobble'),
  http.get(api('/scrobble/status'), () => HttpResponse.json(d.scrobbleStatus)),
  http.put(api('/scrobble/preferences/:service'), async ({ request, params }) =>
    HttpResponse.json({ service: params.service, ...((await request.json()) as object) }),
  ),
  http.post(api('/scrobble/sync'), () => HttpResponse.json({ message: 'Sync started' })),
  http.post(api('/scrobble/auth/:service/disconnect'), () => HttpResponse.json({ message: 'Disconnected' })),
  http.get(api('/settings/importlists'), () => HttpResponse.json(d.importListsSettings)),
  putEcho('/settings/importlists'),
  http.get(api('/importlists'), () => HttpResponse.json(d.importListsStatus)),
  http.put(api('/importlists/prefs'), ok),
  http.post(api('/importlists/run'), () => HttpResponse.json({ started: true }, { status: 202 })),
  http.delete(api('/importlists/skipped/:id'), ok),
  http.post(api('/importlists/skipped/:id/ignore'), ok),
  http.get(api('/notifications'), () => HttpResponse.json(d.notificationConnections)),
  http.get(api('/notifications/providers'), () => HttpResponse.json(d.providers)),
  http.post(api('/notifications'), async ({ request }) =>
    HttpResponse.json({ id: 9, lastAttemptAt: null, lastSuccessAt: null, lastError: null, consecutiveFailures: 0, ...((await request.json()) as object) }),
  ),
  http.put(api('/notifications/:id'), async ({ request, params }) =>
    HttpResponse.json({ id: Number(params.id), lastAttemptAt: null, lastSuccessAt: null, lastError: null, consecutiveFailures: 0, ...((await request.json()) as object) }),
  ),
  http.delete(api('/notifications/:id'), ok),
  http.post(api('/notifications/test'), success),
  http.get(api('/tags'), () => HttpResponse.json(d.tags)),

  // System
  http.get(api('/system/status'), () => HttpResponse.json({ version: '0.32.0' })),
  http.get(api('/system/update'), () => HttpResponse.json(d.updateStatusCurrent)),
  http.get(api('/settings/updates'), () => HttpResponse.json(d.updateSettings)),
  putEcho('/settings/updates'),
  http.post(api('/settings/updates/check'), () => HttpResponse.json(d.updateStatusCurrent)),
  http.get(api('/system/backups'), () => HttpResponse.json(d.backups)),
  http.post(api('/system/backups'), () => HttpResponse.json(d.backups[0])),
  http.delete(api('/system/backups/:name'), ok),
  http.post(api('/system/backups/:name/restore'), () => HttpResponse.json({ message: 'Restoring, Maki will restart.' })),
  http.get(api('/settings/backup'), () => HttpResponse.json(d.backupSettings)),
  putEcho('/settings/backup'),
  http.get(api('/system/image-cache'), () => HttpResponse.json(d.imageCache)),
  http.post(api('/system/image-cache/rebuild'), () => HttpResponse.json({ started: true })),
]

/**
 * Settings stories pass their handlers through this, so the cards see this file's fixtures even
 * where another area's file registers the same route earlier in `defaultHandlers` (Home and Stats
 * both answer `/progress/*`, and the series and reader areas answer `/readingprofiles`).
 */
export const settingsMsw = (...overrides: HttpHandler[]): HttpHandler[] => [...overrides, ...settingsHandlers]
