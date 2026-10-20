import type {
  RatingImportState,
  ScrobbleConnection,
  ScrobbleLogRow,
  ScrobbleStatus,
  ScrobbleSyncRow,
  ScrobbleUnmatchedItem,
} from '../../../src/api/hooks'
import { daysAgo, hoursAgo, minutesAgo } from '../api'
import { seriesById } from '../shared'

const conn = (c: Partial<ScrobbleConnection> & Pick<ScrobbleConnection, 'service' | 'label'>): ScrobbleConnection => ({
  configured: true,
  connected: true,
  username: null,
  oAuth: true,
  syncReading: true,
  syncRatings: true,
  animeList: false,
  animeSignals: true,
  ...c,
})

export const connectedConnections: ScrobbleConnection[] = [
  conn({ service: 'anilist', label: 'AniList', username: 'maki_reads', animeList: true }),
  conn({ service: 'mal', label: 'MyAnimeList', username: 'makireads', animeList: true, syncRatings: false }),
  conn({ service: 'kitsu', label: 'Kitsu', oAuth: false, username: 'maki@example.com', animeList: true, animeSignals: false }),
  conn({ service: 'mangabaka', label: 'MangaBaka', oAuth: false, username: 'maki' }),
  conn({ service: 'mangaupdates', label: 'MangaUpdates', oAuth: false, username: 'makireads', syncRatings: false }),
  conn({ service: 'kavita', label: 'Kavita', oAuth: false, username: 'maki' }),
]

/** One of each state: connected, waiting for sign-in, rejected, not set up. */
export const mixedConnections: ScrobbleConnection[] = [
  connectedConnections[0],
  conn({ service: 'mal', label: 'MyAnimeList', connected: false, animeList: true }),
  conn({ service: 'kitsu', label: 'Kitsu', connected: false, oAuth: false, animeList: true }),
  conn({ service: 'mangabaka', label: 'MangaBaka', configured: false, connected: false, oAuth: false }),
  connectedConnections[4],
  conn({ service: 'kavita', label: 'Kavita', configured: false, connected: false, oAuth: false }),
]

export const disconnectedConnections: ScrobbleConnection[] = connectedConnections.map((c) =>
  conn({ service: c.service, label: c.label, configured: false, connected: false, oAuth: c.oAuth, animeList: c.animeList }),
)

const title = (id: number) => seriesById(id).title

export const recentSyncs: ScrobbleSyncRow[] = [
  { title: title(1), service: 'anilist', chapter: 131, volume: 14, status: 'reading', at: minutesAgo(12), error: null },
  { title: title(1), service: 'mal', chapter: 131, volume: 14, status: 'reading', at: minutesAgo(12), error: null },
  { title: title(1), service: 'kitsu', chapter: 0, volume: 0, status: null, at: minutesAgo(12), error: 'Kitsu answered 429: slow down.' },
  { title: title(1), service: 'mangabaka', chapter: 131, volume: 14, status: 'reading', at: minutesAgo(12), error: null },
  { title: title(1), service: 'mangaupdates', chapter: 131, volume: 14, status: 'reading', at: minutesAgo(12), error: null },
  { title: title(5), service: 'anilist', chapter: 34, volume: 0, status: 'completed', at: hoursAgo(5), error: null },
  { title: title(5), service: 'mal', chapter: 34, volume: 0, status: 'completed', at: hoursAgo(5), error: null },
  { title: title(5), service: 'kitsu', chapter: 34, volume: 0, status: 'completed', at: hoursAgo(5), error: null },
  { title: title(5), service: 'mangabaka', chapter: 34, volume: 0, status: 'completed', at: hoursAgo(5), error: null },
  { title: title(5), service: 'mangaupdates', chapter: 34, volume: 0, status: 'completed', at: hoursAgo(5), error: null },
  { title: title(3), service: 'anilist', chapter: 40, volume: 0, status: 'reading', at: hoursAgo(20), error: null },
  { title: title(3), service: 'mangaupdates', chapter: 40, volume: 0, status: 'reading', at: hoursAgo(20), error: null },
  { title: title(3), service: 'mal', chapter: 38, volume: 0, status: 'reading', at: daysAgo(1), error: null },
  { title: title(7), service: 'mal', chapter: 12, volume: 0, status: 'plan_to_read', at: daysAgo(2), error: null },
  { title: title(2), service: 'anilist', chapter: 96, volume: 11, status: 'completed', at: daysAgo(3), error: null },
  { title: title(2), service: 'mangaupdates', chapter: 96, volume: 11, status: 'completed', at: daysAgo(3), error: null },
]

export const unmatched: ScrobbleUnmatchedItem[] = [
  {
    kavitaSeriesId: 41,
    service: 'anilist',
    title: title(4),
    reason: 'Two close matches and no exact title.',
    candidates: [
      { id: '118201', title: 'Paper Moon Detective', url: 'https://anilist.co/manga/118201' },
      { id: '118944', title: 'Paper Moon Detective: Side Stories', url: 'https://anilist.co/manga/118944' },
    ],
  },
  {
    kavitaSeriesId: 57,
    service: 'mal',
    title: title(12),
    reason: 'No result for this title.',
    candidates: [],
  },
]

export const activityLog: ScrobbleLogRow[] = [
  { timestamp: minutesAgo(12), level: 'info', service: 'anilist', title: title(1), message: 'Progress set to chapter 131.' },
  { timestamp: minutesAgo(12), level: 'info', service: 'mangaupdates', title: title(1), message: 'Progress set to chapter 131.' },
  { timestamp: minutesAgo(12), level: 'error', service: 'kitsu', title: title(1), message: 'HTTP 429 from Kitsu.' },
  { timestamp: minutesAgo(13), level: 'info', service: '', title: '', message: 'Sync started.' },
  { timestamp: hoursAgo(5), level: 'warning', service: 'mal', title: title(12), message: 'No match found, queued for review.' },
  { timestamp: hoursAgo(5), level: 'info', service: 'mangabaka', title: title(5), message: 'Marked completed.' },
  { timestamp: hoursAgo(5), level: 'info', service: 'mangabaka', title: title(5), message: 'Marked completed.' },
]

export const scrobbleStatus: ScrobbleStatus = {
  connections: connectedConnections,
  running: false,
  lastSyncAt: minutesAgo(12),
  nextSyncAt: minutesAgo(-18),
  intervalMinutes: 30,
  planToRead: true,
  recent: recentSyncs,
  unmatched,
  log: activityLog,
}

export const scrobbleStatusQuiet: ScrobbleStatus = {
  ...scrobbleStatus,
  recent: [],
  unmatched: [],
  log: [],
  lastSyncAt: null,
}

export const scrobbleStatusDisconnected: ScrobbleStatus = {
  ...scrobbleStatusQuiet,
  connections: disconnectedConnections,
  nextSyncAt: null,
}

export const scrobbleStatusMixed: ScrobbleStatus = { ...scrobbleStatus, connections: mixedConnections }

export const importItems: RatingImportState['items'] = [
  { seriesId: 1, title: title(1), localRating: null, remoteScore: 9 },
  { seriesId: 2, title: title(2), localRating: 7, remoteScore: 8 },
  { seriesId: 5, title: title(5), localRating: 9, remoteScore: 10 },
  { seriesId: 6, title: title(6), localRating: null, remoteScore: 6 },
  { seriesId: 10, title: title(10), localRating: 8, remoteScore: 9 },
  { seriesId: 12, title: title(12), localRating: 6, remoteScore: 5 },
]

export const importReady: RatingImportState = { running: false, computedAt: minutesAgo(1), error: null, items: importItems }
export const importRunning: RatingImportState = { running: true, computedAt: null, error: null, items: [] }
export const importEmpty: RatingImportState = { running: false, computedAt: minutesAgo(1), error: null, items: [] }
export const importFailed: RatingImportState = {
  running: false,
  computedAt: minutesAgo(1),
  error: 'AniList refused the saved token. Reconnect and try again.',
  items: [],
}
