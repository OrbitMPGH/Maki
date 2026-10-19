import { http, HttpResponse, type HttpHandler } from 'msw'
import type { ProgressSummary, ReadingGoal, ScrobbleStatus, RatingImportState } from '../../src/api/hooks'
import type { RootFolder } from '../../src/api/types'
import type { StatsSectionProps } from '../../src/pages/stats/sections/types'
import { api } from './api'
import {
  achievements,
  activityFor,
  heatmapDays,
  insightsFor,
  leaderboard,
  libraryFor,
  progressSettings,
  progressSummaryRich,
  standingFor,
  users,
  type StatsKind,
} from './insights/stats'
import { importReady, scrobbleStatus } from './insights/scrobble'

export * from './insights/stats'
export * from './insights/scrobble'

const rootFolders: RootFolder[] = [{ id: 1, path: '/library', freeSpace: 412_000_000_000, accessible: true }]

const range = (request: Request) => {
  const params = new URL(request.url).searchParams
  return { from: params.get('from') ?? '2026-09-20', to: params.get('to') ?? '2026-10-19' }
}

export const activityHandler = (kind: StatsKind = 'rich') =>
  http.get(api('/stats/activity'), ({ request }) => {
    const { from, to } = range(request)
    return HttpResponse.json(activityFor(from, to, kind))
  })

export const insightsHandler = (kind: StatsKind = 'rich') =>
  http.get(api('/stats/insights'), ({ request }) => {
    const { from, to } = range(request)
    return HttpResponse.json(insightsFor(from, to, kind))
  })

export const standingHandler = (kind: StatsKind = 'rich') =>
  http.get(api('/stats/standing'), () => HttpResponse.json(standingFor(kind)))

export const libraryHandler = (kind: 'rich' | 'empty' = 'rich') =>
  http.get(api('/stats/library'), () => HttpResponse.json(libraryFor(kind)))

export const yearsHandler = (years: number[] = [2024, 2025, 2026]) =>
  http.get(api('/stats/years'), () => HttpResponse.json(years))

/** Home registers `/progress/summary` for every story and wins on order, so Stats stories pass this explicitly. */
export const progressSummaryHandler = (summary: ProgressSummary = progressSummaryRich) =>
  http.get(api('/progress/summary'), () => HttpResponse.json(summary))

/** Settings also registers `/users`, so the Stats page passes its own to keep the reader picker predictable. */
export const usersHandler = () => http.get(api('/users'), () => HttpResponse.json(users))

/** What the whole Stats page needs beyond the stats endpoints: progress summary, accounts, root folders. */
export const statsPageBase = (summary: ProgressSummary = progressSummaryRich): HttpHandler[] => [
  progressSummaryHandler(summary),
  usersHandler(),
  http.get(api('/rootfolder'), () => HttpResponse.json(rootFolders)),
]

/** The three per-reader payloads for one kind of reader. */
export const statsHandlers = (kind: StatsKind): HttpHandler[] => [
  activityHandler(kind),
  insightsHandler(kind),
  standingHandler(kind),
]

export const scrobbleStatusHandler = (status: ScrobbleStatus = scrobbleStatus) =>
  http.get(api('/scrobble/status'), () => HttpResponse.json(status))

/** Preview, poll and apply for one tracker. `state` is what the poll answers with. */
export const ratingImportHandlers = (state: RatingImportState = importReady): HttpHandler[] => [
  http.post(api('/scrobble/import-ratings/:service/preview'), () => HttpResponse.json({ started: true })),
  http.get(api('/scrobble/import-ratings/:service'), () => HttpResponse.json(state)),
  http.post(api('/scrobble/import-ratings/:service/apply'), async ({ request }) => {
    const body = (await request.json()) as { seriesIds: number[] }
    return HttpResponse.json({ applied: body.seriesIds.length })
  }),
]

/** What the page hands every section, for a 30 day window ending on the story clock. */
export const sectionProps: StatsSectionProps = {
  range: { from: '2026-09-20', to: '2026-10-19' },
  previous: { from: '2026-08-21', to: '2026-09-19' },
  preset: '30d',
  windowLabel: 'the last 30 days',
}


/**
 * Every scrobble route. Settings registers its own `/scrobble/status` for every story and wins on
 * order, so Scrobble page stories pass this explicitly.
 */
export const scrobbleHandlers = (status: ScrobbleStatus = scrobbleStatus): HttpHandler[] => [
  scrobbleStatusHandler(status),
  http.post(api('/scrobble/sync'), () => HttpResponse.json({ message: 'Sync started.' })),
  http.post(api('/scrobble/match'), () => HttpResponse.json({ message: 'Matched and queued for the next sync.' })),
  http.post(api('/scrobble/ignore'), () => HttpResponse.json({ message: 'Ignored.' })),
  http.get(api('/scrobble/auth/:service/start'), ({ params }) =>
    HttpResponse.json({ url: `/mock/oauth/${String(params.service)}` }),
  ),
  http.post(api('/scrobble/auth/:service/disconnect'), () => HttpResponse.json({ message: 'Disconnected.' })),
  http.put(api('/scrobble/preferences/:service'), async ({ params, request }) => {
    const body = (await request.json()) as { reading: boolean; ratings: boolean; anime?: boolean }
    return HttpResponse.json({ service: String(params.service), reading: body.reading, ratings: body.ratings, anime: body.anime ?? null })
  }),
]

let nextGoalId = 40

export const insightsHandlers: HttpHandler[] = [
  yearsHandler(),
  activityHandler(),
  insightsHandler(),
  standingHandler(),
  libraryHandler(),

  http.get(api('/progress/achievements'), () => HttpResponse.json(achievements)),
  http.get(api('/progress/heatmap'), () => HttpResponse.json(heatmapDays)),
  http.get(api('/progress/leaderboard'), () => HttpResponse.json(leaderboard)),
  http.get(api('/progress/settings'), () => HttpResponse.json(progressSettings)),
  http.put(api('/progress/settings'), async ({ request }) => HttpResponse.json(await request.json())),
  http.put(api('/progress/goals'), async ({ request }) => {
    const goal = (await request.json()) as Pick<ReadingGoal, 'period' | 'metric' | 'target'>
    return HttpResponse.json([...progressSummaryRich.goals, { id: nextGoalId++, progress: 0, ...goal }] satisfies ReadingGoal[])
  }),
  http.delete(api('/progress/goals/:id'), () => new HttpResponse(null, { status: 204 })),

  ...scrobbleHandlers(),
  ...ratingImportHandlers(),

  usersHandler(),
  http.get(api('/rootfolder'), () => HttpResponse.json(rootFolders)),
]
