import { http, HttpResponse } from 'msw'
import type { HttpHandler } from 'msw'
import type { Announcements, ProgressSummary } from '../../src/api/hooks'
import type { UpdateStatusDto } from '../../src/api/types'
import { api, daysAgo } from './api'
import { inboxHandlers } from './shell/inbox'
import { idleDump } from './shell/wizard'

export const noAnnouncements: Announcements = { language: false, appearance: false }

export const upToDate: UpdateStatusDto = {
  currentVersion: '0.32.0',
  isDevBuild: false,
  isDocker: true,
  updateAvailable: false,
  latestVersion: '0.32.0',
  releaseUrl: null,
  releaseNotes: null,
  checkedAt: daysAgo(0),
}

export const updateAvailable: UpdateStatusDto = {
  ...upToDate,
  updateAvailable: true,
  latestVersion: '0.33.0',
  releaseUrl: 'https://github.com/OrbitMPGH/Maki/releases/tag/v0.33.0',
  releaseNotes: 'Chapter upgrades, offline reading and a refreshed Home.',
}

export const progressSummary: ProgressSummary = {
  enabled: true,
  showStreaks: true,
  level: { level: 12, xp: 4_620, intoLevel: 310, levelSpan: 660, nextLevelXp: 4_970, progress: 0.47 },
  chaptersRead: 1_284,
  readingSeconds: 412_000,
  seriesFinished: 23,
  daysRead: 186,
  currentStreak: 9,
  longestStreak: 31,
  earned: 14,
  total: 40,
  recent: [],
  goals: [],
}

/** Endpoints the app chrome (bell, account menu, sidebar footer, announcements) reads. */
export const shellHandlers: HttpHandler[] = [
  ...inboxHandlers(),
  http.get(api('/settings/announcements'), () => HttpResponse.json(noAnnouncements)),
  http.post(api('/settings/announcements/:key/seen'), () => new HttpResponse(null, { status: 204 })),
  http.get(api('/settings/metadata/dump-progress'), () => HttpResponse.json(idleDump)),
  http.get(api('/system/status'), () => HttpResponse.json({ version: '0.32.0' })),
  http.get(api('/system/update'), () => HttpResponse.json(upToDate)),
  http.get(api('/progress/summary'), () => HttpResponse.json(progressSummary)),
  http.get(api('/reader/used'), () => HttpResponse.json({ used: true, kavita: false })),
  http.put(api('/settings/ui'), async ({ request }) => HttpResponse.json(await request.json())),
  http.post(api('/auth/logout'), () => new HttpResponse(null, { status: 204 })),
]
