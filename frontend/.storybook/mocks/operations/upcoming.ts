import { http, HttpResponse, type HttpHandler } from 'msw'
import type { ReleaseOutlookDto, UpcomingSeriesDto } from '../../../src/api/releases'
import { api, cover, daysAgo, daysAhead, hoursAgo } from '../api'
import { catalogue } from '../shared'

const base: ReleaseOutlookDto = {
  state: 'Expected',
  releaseDays: 24,
  interval: 7,
  spread: 0,
  lastRelease: daysAgo(3),
  windowStart: daysAhead(3),
  windowEnd: daysAhead(5),
  confidence: 0.83,
  hits: 9,
  checked: 10,
  announcedAt: null,
  announcedBy: null,
  refreshing: true,
  refreshTier: 'Active',
  lastCheckAt: hoursAgo(1),
  nextCheckAt: daysAhead(1 / 24),
}

export const expectedOutlook = base

export const announcedOutlook: ReleaseOutlookDto = {
  ...base,
  announcedAt: daysAhead(4),
  announcedBy: 'MANGA Plus',
}

export const mediumOutlook: ReleaseOutlookDto = {
  ...base,
  interval: 14,
  spread: 2,
  windowStart: daysAhead(9),
  windowEnd: daysAhead(13),
  confidence: 0.58,
  hits: 6,
  checked: 10,
  refreshTier: 'Slowing',
}

export const lateOutlook: ReleaseOutlookDto = {
  ...base,
  state: 'Late',
  lastRelease: daysAgo(11),
  windowStart: daysAgo(5),
  windowEnd: daysAgo(3),
}

export const irregularOutlook: ReleaseOutlookDto = {
  ...base,
  state: 'Irregular',
  interval: 12,
  spread: 9,
  windowStart: null,
  windowEnd: null,
  confidence: 0.33,
  hits: 2,
  checked: 10,
  refreshTier: 'Slowing',
}

export const unknownOutlook: ReleaseOutlookDto = {
  ...base,
  state: 'Unknown',
  releaseDays: 2,
  interval: null,
  spread: null,
  windowStart: null,
  windowEnd: null,
  confidence: 0.5,
  hits: 0,
  checked: 0,
  refreshTier: 'Unknown',
}

export const finishedOutlook: ReleaseOutlookDto = {
  ...irregularOutlook,
  state: 'Stalled',
  refreshing: false,
  nextCheckAt: null,
}

/** Series page outlook by catalogue id, so the panel varies across the series stories. */
export const outlookFor = (id: number): ReleaseOutlookDto =>
  [announcedOutlook, expectedOutlook, irregularOutlook, mediumOutlook, lateOutlook][(id - 1) % 5]

const entry = (index: number, outlook: ReleaseOutlookDto): UpcomingSeriesDto => {
  const s = catalogue[index % catalogue.length]
  return { seriesId: s.id, title: s.title, coverUrl: cover(s.id), outlook }
}

export const upcomingItems: UpcomingSeriesDto[] = [
  entry(0, announcedOutlook),
  entry(1, { ...base, windowStart: daysAgo(1), windowEnd: daysAhead(1) }),
  entry(2, base),
  entry(3, mediumOutlook),
  entry(4, { ...mediumOutlook, interval: 30, windowStart: daysAhead(18), windowEnd: daysAhead(24), confidence: 0.67 }),
  entry(5, { ...base, interval: 30, windowStart: daysAhead(40), windowEnd: daysAhead(46) }),
  entry(6, lateOutlook),
  entry(7, { ...irregularOutlook, announcedAt: daysAhead(8), announcedBy: 'MANGA Plus' }),
]

export const upcomingHandlers: HttpHandler[] = [http.get(api('/upcoming'), () => HttpResponse.json(upcomingItems))]
