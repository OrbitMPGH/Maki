import type {
  Achievement,
  ActivityDroppedSeries,
  ActivitySeriesEvent,
  ActivityStats,
  ActivityTimelinePoint,
  HeatmapDay,
  LeaderboardRow,
  LibraryComposition,
  LibraryGrowth,
  ProgressSettings,
  ProgressSummary,
  ReadingBehaviour,
} from '../../../src/api/hooks'
import type { StatsInsightsDto, StatsStandingDto } from '../../../src/api/stats'
import type { UserSummary } from '../../../src/api/auth'
import { adminMe, cover, daysAgo, readerMe } from '../api'
import { catalogue, seriesById } from '../shared'

export type StatsKind = 'rich' | 'sparse' | 'untracked'

/** Deterministic 0..1 noise, so a chart keeps its shape between runs. */
export const noise = (i: number) => ((i * 9301 + 49297) % 233280) / 233280

const DAY = 86_400_000
const utc = (iso: string) => new Date(`${iso}T00:00:00Z`)
const ymd = (d: Date) => d.toISOString().slice(0, 10)

/** Requests for a window that ends before this are the "previous period" fetch. */
const PREVIOUS_CUTOFF = '2026-10-15'

const ref = (id: number) => ({ seriesId: id, title: seriesById(id).title, coverUrl: cover(id) })

function timelineFor(from: string, to: string, scale: number): ActivityTimelinePoint[] {
  const start = utc(from)
  const end = utc(to)
  const days = Math.round((end.getTime() - start.getTime()) / DAY) + 1
  const points: ActivityTimelinePoint[] = []
  if (days <= 62) {
    for (let i = 0; i < days; i++) {
      const n = noise(i + 3)
      const chapters = n < 0.3 ? 0 : Math.round((1 + Math.floor(n * 9)) * scale)
      points.push({
        bucket: ymd(new Date(start.getTime() + i * DAY)),
        chaptersRead: chapters,
        chaptersDownloaded: chapters > 0 ? Math.round(chapters * 0.6) : 0,
        seriesAdded: i % 11 === 0 ? 1 : 0,
        readingSeconds: chapters * 410,
      })
    }
    return points
  }
  const cursor = new Date(Date.UTC(start.getUTCFullYear(), start.getUTCMonth(), 1))
  let i = 0
  while (cursor <= end) {
    const chapters = Math.round((20 + Math.floor(noise(i + 7) * 70)) * scale)
    points.push({
      bucket: `${cursor.getUTCFullYear()}-${String(cursor.getUTCMonth() + 1).padStart(2, '0')}`,
      chaptersRead: chapters,
      chaptersDownloaded: Math.round(chapters * 0.8),
      seriesAdded: 1 + (i % 3),
      readingSeconds: chapters * 395,
    })
    cursor.setUTCMonth(cursor.getUTCMonth() + 1)
    i++
  }
  return points
}

export const feedFinished: ActivitySeriesEvent[] = [2, 10].map((id, i) => ({
  ...ref(id),
  at: daysAgo(3 + i * 9),
  providerId: `mb-${seriesById(id).mangaBakaId}`,
}))
export const feedAdded: ActivitySeriesEvent[] = [9, 11, 3].map((id, i) => ({
  ...ref(id),
  at: daysAgo(5 + i * 6),
  providerId: `mb-${seriesById(id).mangaBakaId}`,
}))
export const feedRemoved: ActivitySeriesEvent[] = [12, 3].map((id, i) => ({
  ...ref(id),
  at: daysAgo(15 + i * 9),
  providerId: `mb-${seriesById(id).mangaBakaId}`,
}))
export const feedDropped: ActivityDroppedSeries[] = [
  { ...ref(7), lastProgressAt: daysAgo(21), maxChapter: 12 },
  { ...ref(11), lastProgressAt: daysAgo(34), maxChapter: 20 },
]

export function activityFor(from: string, to: string, kind: StatsKind): ActivityStats {
  const base = { from, to }
  const zero = {
    chaptersRead: 0,
    volumesRead: 0,
    chaptersDownloaded: 0,
    seriesAdded: 0,
    seriesRemoved: 0,
    seriesFinished: 0,
    seriesDropped: 0,
    readingSeconds: 0,
    daysActive: 0,
    pagesRead: 0,
    seriesStarted: 0,
  }
  if (kind === 'untracked') {
    return {
      ...base,
      readTrackingAvailable: false,
      totals: zero,
      timeline: [],
      topRead: [],
      leastRead: [],
      topGenres: [],
      topTags: [],
      finished: [],
      added: [],
      removed: [],
      dropped: [],
      topByTime: [],
    }
  }
  if (kind === 'sparse') {
    return {
      ...base,
      readTrackingAvailable: true,
      totals: { ...zero, chaptersRead: 4, readingSeconds: 1500, daysActive: 2, pagesRead: 96, seriesStarted: 1, seriesAdded: 2 },
      timeline: [
        { bucket: '2026-10-17', chaptersRead: 3, chaptersDownloaded: 3, seriesAdded: 2, readingSeconds: 1100 },
        { bucket: '2026-10-18', chaptersRead: 1, chaptersDownloaded: 0, seriesAdded: 0, readingSeconds: 400 },
      ],
      topRead: [{ seriesId: 5, title: seriesById(5).title, coverUrl: cover(5), count: 4 }],
      leastRead: [],
      topGenres: [{ name: 'Romance', weight: 4 }],
      topTags: [],
      finished: [],
      added: [{ ...ref(5), at: daysAgo(2), providerId: null }],
      removed: [],
      dropped: [],
      topByTime: [{ seriesId: 5, title: seriesById(5).title, seconds: 1500, coverUrl: cover(5) }],
    }
  }

  const scale = to <= PREVIOUS_CUTOFF ? 0.8 : 1
  const timeline = timelineFor(from, to, scale)
  const chaptersRead = timeline.reduce((sum, p) => sum + p.chaptersRead, 0)
  const readingSeconds = timeline.reduce((sum, p) => sum + p.readingSeconds, 0)
  const daily = timeline.length > 0 && timeline[0].bucket.length === 10
  const daysActive = daily ? timeline.filter((p) => p.chaptersRead > 0).length : Math.round(chaptersRead / 4.2)
  const share = (fraction: number) => Math.max(1, Math.round(chaptersRead * fraction))

  return {
    ...base,
    readTrackingAvailable: true,
    totals: {
      chaptersRead,
      volumesRead: Math.round(chaptersRead / 12),
      chaptersDownloaded: timeline.reduce((sum, p) => sum + p.chaptersDownloaded, 0),
      seriesAdded: timeline.reduce((sum, p) => sum + p.seriesAdded, 0),
      seriesRemoved: 2,
      seriesFinished: Math.round(2 * scale),
      seriesDropped: 2,
      readingSeconds,
      daysActive,
      pagesRead: chaptersRead * 24,
      seriesStarted: Math.round(5 * scale),
    },
    timeline,
    topRead: [
      [1, 0.32],
      [6, 0.22],
      [5, 0.17],
      [9, 0.12],
      [3, 0.09],
    ].map(([id, f]) => ({ seriesId: id, title: seriesById(id).title, coverUrl: cover(id), count: share(f) })),
    leastRead: [
      [7, 0.01],
      [11, 0.01],
      [2, 0.02],
    ].map(([id, f]) => ({ seriesId: id, title: seriesById(id).title, coverUrl: cover(id), count: share(f) })),
    topGenres: [
      { name: 'Fantasy', weight: 54 },
      { name: 'Adventure', weight: 31 },
      { name: 'Romance', weight: 22 },
      { name: 'Drama', weight: 19 },
      { name: 'Action', weight: 14 },
      { name: 'Slice of Life', weight: 9 },
    ],
    topTags: [
      { name: 'Found Family', weight: 40 },
      { name: 'Travel', weight: 33 },
      { name: 'Magic', weight: 28 },
      { name: 'Seaside', weight: 17 },
      { name: 'Regression', weight: 15 },
      { name: 'Royalty', weight: 11 },
      { name: 'Dungeon', weight: 6 },
      { name: 'Letters', weight: 4 },
    ],
    finished: feedFinished,
    added: feedAdded,
    removed: feedRemoved,
    dropped: feedDropped,
    topByTime: [
      [1, 0.34],
      [6, 0.2],
      [5, 0.18],
    ].map(([id, f]) => ({ seriesId: id, title: seriesById(id).title, seconds: Math.round(readingSeconds * f), coverUrl: cover(id) })),
  }
}

/** Enough finished events to pass the feed's first page of twenty. */
export const longFeed: ActivityStats = {
  ...activityFor('2026-09-20', '2026-10-19', 'rich'),
  finished: Array.from({ length: 24 }, (_, i) => {
    const entry = catalogue[i % catalogue.length]
    return { ...ref(entry.id), at: daysAgo(i + 1), providerId: null }
  }),
}

/** Evenings heavy, weekends heavier, almost nothing in the small hours. */
function rhythmBuckets(): number[] {
  const out: number[] = []
  for (let day = 0; day < 7; day++) {
    for (let hour = 0; hour < 24; hour++) {
      const evening = hour >= 20 || hour <= 1 ? 1 : hour >= 12 && hour <= 14 ? 0.35 : hour >= 7 && hour <= 8 ? 0.2 : 0.03
      const weekend = day >= 5 ? 1.6 : 1
      out.push(Math.round(evening * weekend * (900 + noise(day * 24 + hour) * 2400)))
    }
  }
  return out
}

export const richRhythm = rhythmBuckets()

export function insightsFor(from: string, to: string, kind: StatsKind): StatsInsightsDto {
  const base = { from, to, timeZone: 'UTC' }
  if (kind !== 'rich') {
    return {
      ...base,
      rhythm: {
        secondsByWeekdayHour: Array.from({ length: 168 }, () => 0),
        totalSeconds: 0,
        primeStartHour: null,
        primeShare: null,
        busiestWeekday: null,
        weekendShare: null,
      },
      sittings: null,
      taste: { lean: [], types: [], demographics: [], eras: [] },
      bookmarksAdded: 0,
    }
  }
  return {
    ...base,
    rhythm: {
      secondsByWeekdayHour: richRhythm,
      totalSeconds: richRhythm.reduce((a, b) => a + b, 0),
      primeStartHour: 21,
      primeShare: 0.41,
      busiestWeekday: 5,
      weekendShare: 0.38,
    },
    sittings: { count: 64, medianSeconds: 1380, longestSeconds: 9300, longestStartedAt: daysAgo(12), perWeek: 5.6, chaptersPerSittingMedian: 3.5 },
    taste: {
      lean: [
        { name: 'Fantasy', readShare: 0.34, libraryShare: 0.21 },
        { name: 'Romance', readShare: 0.18, libraryShare: 0.12 },
        { name: 'Adventure', readShare: 0.16, libraryShare: 0.14 },
        { name: 'Slice of Life', readShare: 0.08, libraryShare: 0.13 },
        { name: 'Comedy', readShare: 0.04, libraryShare: 0.11 },
        { name: 'Sports', readShare: 0.01, libraryShare: 0.09 },
        { name: 'Horror', readShare: 0.07, libraryShare: 0.05 },
        { name: 'Mystery', readShare: 0.12, libraryShare: 0.15 },
      ],
      types: [
        { name: 'Manga', count: 312 },
        { name: 'Manhwa', count: 148 },
        { name: 'Manhua', count: 37 },
      ],
      demographics: [
        { name: 'Shounen', count: 190 },
        { name: 'Seinen', count: 120 },
        { name: 'Josei', count: 64 },
        { name: '', count: 45 },
      ],
      eras: [
        { decade: 2010, chapters: 120 },
        { decade: 2020, chapters: 310 },
        { decade: 2000, chapters: 38 },
        { decade: null, chapters: 12 },
      ],
    },
    bookmarksAdded: 7,
  }
}

const behaviour = (rich: boolean): ReadingBehaviour => ({
  seriesStarted: rich ? 24 : 1,
  seriesFinished: rich ? 9 : 0,
  finishRate: rich ? 0.375 : null,
  medianStopPoint: rich ? 0.34 : null,
  medianSecondsPerChapter: rich ? 420 : null,
  timedChapters: rich ? 640 : 0,
  chaptersRead: rich ? 1284 : 4,
  readingDays: rich ? 118 : 2,
  medianChaptersPerReadingDay: rich ? 6 : null,
  biggestDayCount: rich ? 38 : null,
  biggestDay: rich ? '2026-08-09' : null,
  savoured: rich
    ? [
        { ...ref(10), value: '', measure: 1180 },
        { ...ref(1), value: '', measure: 960 },
        { ...ref(5), value: '', measure: 780 },
      ]
    : [],
  devoured: rich
    ? [
        { ...ref(8), value: '', measure: 130 },
        { ...ref(2), value: '', measure: 190 },
      ]
    : [],
  abandoned: rich
    ? [
        { ...ref(7), value: '', measure: 0 },
        { ...ref(11), value: '', measure: 0 },
        { ...ref(12), value: '', measure: 0 },
      ]
    : [],
  stopPointHistogram: rich ? [3, 4, 3, 2, 1, 1, 0, 1, 0, 0] : [0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
  generatedAt: daysAgo(0),
})

export function standingFor(kind: StatsKind): StatsStandingDto {
  if (kind !== 'rich') {
    return {
      behaviour: behaviour(false),
      backlog: { unreadChapters: 0, seriesWithUnread: 0, hoursAtPace: null, top: [] },
      midway: [],
      creators: [],
      ratings: null,
      seriesFullyRead: 0,
    }
  }
  return {
    behaviour: behaviour(true),
    backlog: {
      unreadChapters: 482,
      seriesWithUnread: 7,
      hoursAtPace: 56,
      top: [
        { ...ref(8), read: 0, unread: 210 },
        { ...ref(7), read: 12, unread: 108 },
        { ...ref(3), read: 40, unread: 48 },
        { ...ref(6), read: 150, unread: 53 },
        { ...ref(11), read: 20, unread: 37 },
        { ...ref(1), read: 131, unread: 11 },
      ],
    },
    midway: [
      { ...ref(3), read: 40, held: 48, etaSeconds: 20_160, lastReadAt: daysAgo(2) },
      { ...ref(6), read: 150, held: 53, etaSeconds: 22_260, lastReadAt: daysAgo(1) },
      { ...ref(11), read: 20, held: 37, etaSeconds: null, lastReadAt: daysAgo(15) },
      { ...ref(7), read: 12, held: 108, etaSeconds: 45_360, lastReadAt: daysAgo(30) },
    ],
    creators: [
      { name: 'Aoi Mizuhara', story: true, art: true, seriesRead: 2, chaptersRead: 179 },
      { name: 'Mei Okabe', story: false, art: true, seriesRead: 1, chaptersRead: 96 },
      { name: 'Haru Takeda', story: true, art: false, seriesRead: 1, chaptersRead: 96 },
      { name: 'Studio Lark', story: false, art: true, seriesRead: 1, chaptersRead: 150 },
    ],
    ratings: {
      meanGap: 0.6,
      rated: 7,
      series: [
        { ...ref(10), yours: 9.5, community: 8.9 },
        { ...ref(1), yours: 9, community: 8.2 },
        { ...ref(5), yours: 8, community: 8.4 },
        { ...ref(6), yours: 6, community: 7.5 },
        { ...ref(12), yours: 5, community: 6.1 },
        { ...ref(2), yours: 8, community: 8 },
      ],
    },
    seriesFullyRead: 9,
  }
}

export const sourceUsage = [
  { name: 'MangaDex', files: 612, bytes: 18_400_000_000 },
  { name: 'Asura Scans', files: 291, bytes: 9_100_000_000 },
  { name: 'Comick', files: 203, bytes: 5_600_000_000 },
  { name: 'Weeb Central', files: 44, bytes: 1_100_000_000 },
  { name: 'torrent', files: 12, bytes: 3_900_000_000 },
]

export const sourceReliability = [
  { name: 'MangaDex', completed: 412, failed: 9, medianSecondsToComplete: 38 },
  { name: 'Asura Scans', completed: 120, failed: 41, medianSecondsToComplete: 95 },
  { name: 'Comick', completed: 88, failed: 3, medianSecondsToComplete: 52 },
  { name: 'torrent', completed: 4, failed: 0, medianSecondsToComplete: 1260 },
]

/** A point per day up to 62 days, else per month, ending at `to`, like the server. */
export function libraryGrowthFor(from: string, to: string, kind: 'rich' | 'empty'): LibraryGrowth[] {
  if (kind === 'empty') return []
  const start = new Date(`${from}T00:00:00`)
  const end = new Date(`${to}T00:00:00`)
  const byDay = Math.round((end.getTime() - start.getTime()) / 86_400_000) + 1 <= 62
  const pad = (n: number) => String(n).padStart(2, '0')
  const points: LibraryGrowth[] = []
  let series = 40
  let bytes = 9_000_000_000
  const cursor = byDay ? start : new Date(start.getFullYear(), start.getMonth(), 1)
  for (let i = 0; cursor <= end; i++) {
    const seriesAdded = byDay ? (noise(i) > 0.8 ? 1 : 0) : 3 + Math.floor(noise(i) * 9)
    const bytesAdded = Math.round((byDay ? 0.05 : 1.2) * (0.4 + noise(i + 3) * 1.6) * 1_000_000_000)
    series += seriesAdded
    bytes += bytesAdded
    const month = `${cursor.getFullYear()}-${pad(cursor.getMonth() + 1)}`
    points.push({
      bucket: byDay ? `${month}-${pad(cursor.getDate())}` : month,
      seriesAdded,
      cumulative: series,
      bytesAdded,
      cumulativeBytes: bytes,
    })
    if (byDay) cursor.setDate(cursor.getDate() + 1)
    else cursor.setMonth(cursor.getMonth() + 1)
  }
  return points
}

export function libraryFor(kind: 'rich' | 'empty'): LibraryComposition {
  if (kind === 'empty') {
    return {
      totals: { seriesCount: 0, monitoredCount: 0, completedCount: 0, chapterCount: 0, downloadedChapterCount: 0, fileCount: 0, totalBytes: 0 },
      byType: [],
      byStatus: [],
      bySource: [],
      topGenres: [],
      largest: [],
      byContentRating: [],
      sourceReliability: [],
      requests: { allUsers: false, open: 0, resolved90d: 0, medianResolveHours: null },
      monitorCatches: 0,
    }
  }
  return {
    totals: { seriesCount: 98, monitoredCount: 71, completedCount: 24, chapterCount: 6420, downloadedChapterCount: 5873, fileCount: 1162, totalBytes: 38_100_000_000 },
    byType: [
      { name: 'Manga', count: 61 },
      { name: 'Manhwa', count: 26 },
      { name: 'Manhua', count: 9 },
      { name: 'unknown', count: 2 },
    ],
    byStatus: [
      { name: 'Ongoing', count: 52 },
      { name: 'Completed', count: 30 },
      { name: 'Hiatus', count: 9 },
      { name: 'Cancelled', count: 7 },
    ],
    bySource: sourceUsage,
    topGenres: [
      { name: 'Fantasy', count: 41 },
      { name: 'Action', count: 33 },
      { name: 'Romance', count: 27 },
      { name: 'Slice of Life', count: 22 },
      { name: 'Drama', count: 18 },
      { name: 'Comedy', count: 15 },
    ],
    largest: [
      { ...ref(8), files: 210, bytes: 6_400_000_000 },
      { ...ref(6), files: 203, bytes: 5_100_000_000 },
      { ...ref(1), files: 142, bytes: 3_800_000_000 },
      { ...ref(2), files: 96, bytes: 2_200_000_000 },
    ],
    byContentRating: [
      { name: 'Safe', count: 64 },
      { name: 'Suggestive', count: 22 },
      { name: 'unknown', count: 12 },
    ],
    sourceReliability,
    requests: { allUsers: true, open: 3, resolved90d: 11, medianResolveHours: 18 },
    monitorCatches: 134,
  }
}

const ach = (a: Partial<Achievement> & Pick<Achievement, 'key' | 'name' | 'description' | 'icon'>): Achievement => ({
  track: 'Reader',
  graded: true,
  hidden: false,
  tier: 0,
  tierName: null,
  value: 0,
  nextThreshold: null,
  tiers: [10, 50, 200, 1000],
  unlockedAt: null,
  unlockId: null,
  ...a,
})

export const achievements: Achievement[] = [
  ach({ key: 'chapters', name: 'Page Turner', description: 'Read chapters.', icon: 'book', tier: 3, tierName: 'Gold', value: 612, nextThreshold: 1000, unlockedAt: daysAgo(40) }),
  ach({ key: 'marathoner', name: 'Marathoner', description: 'Spend time in the reader.', icon: 'clock', tier: 2, tierName: 'Silver', value: 213_000, nextThreshold: 360_000, tiers: [36_000, 108_000, 360_000], unlockedAt: daysAgo(60) }),
  ach({ key: 'finisher', name: 'Finisher', description: 'Finish series.', icon: 'flag', tier: 1, tierName: 'Bronze', value: 9, nextThreshold: 25, tiers: [5, 25, 75], unlockedAt: daysAgo(90) }),
  ach({ key: 'explorer', name: 'Explorer', description: 'Read across genres.', icon: 'compass', tier: 0, value: 3, nextThreshold: 8, tiers: [8, 15, 25] }),
  ach({ key: 'streak', name: 'On a Roll', description: 'Read on consecutive days.', icon: 'flame', tier: 2, tierName: 'Silver', value: 31, nextThreshold: 60, tiers: [7, 30, 60, 100], unlockedAt: daysAgo(20) }),
  ach({ key: 'night-owl', name: 'Night Owl', description: 'Finish a chapter after midnight.', icon: 'owl', graded: false, tier: 1, value: 1, tiers: [1], unlockedAt: daysAgo(100) }),
  ach({ key: 'early-bird', name: 'Early Bird', description: 'Finish a chapter before 6am.', icon: 'sunrise', graded: false, tier: 0, value: 0, tiers: [1] }),
  ach({ key: 'ghost', name: 'Ghost Reader', description: 'Read in incognito.', icon: 'ghost', graded: false, tier: 1, value: 1, tiers: [1], unlockedAt: daysAgo(12) }),
  ach({ key: 'calendar', name: 'Regular', description: 'Read on distinct days.', icon: 'calendar', tier: 3, tierName: 'Gold', value: 118, nextThreshold: 365, tiers: [10, 50, 100, 365], unlockedAt: daysAgo(15) }),
  ach({ key: 'tomes', name: 'Tome Keeper', description: 'Grow the library.', icon: 'library', track: 'Library', tier: 4, tierName: 'Platinum', value: 98, nextThreshold: null, tiers: [10, 25, 50, 100], unlockedAt: daysAgo(200) }),
  ach({ key: 'hoard', name: 'Hoarder', description: 'Download chapters.', icon: 'download', track: 'Library', tier: 2, tierName: 'Silver', value: 5873, nextThreshold: 10_000, tiers: [500, 2500, 10_000], unlockedAt: daysAgo(70) }),
  ach({ key: 'mystery', name: 'Hidden Badge', description: 'Something unexpected.', icon: 'unknown', graded: false, hidden: true, tier: 0, value: 0, tiers: [1], track: 'Library' }),
]

export const heatmapDays: HeatmapDay[] = Array.from({ length: 365 }, (_, i) => {
  const n = noise(i + 11)
  const chapters = n < 0.45 ? 0 : n < 0.75 ? 1 + Math.floor(n * 4) : n < 0.93 ? 4 + Math.floor(n * 8) : 12 + Math.floor(n * 14)
  return { date: daysAgo(i).slice(0, 10), chapters, seconds: chapters * 400 }
}).filter((d) => d.chapters > 0)

export const leaderboard: LeaderboardRow[] = [
  { userId: 1, name: 'Maki', level: 12, chaptersRead: 1284, currentStreak: 6 },
  { userId: 2, name: 'Rin', level: 9, chaptersRead: 842, currentStreak: 14 },
  { userId: 3, name: null, level: 4, chaptersRead: 190, currentStreak: 0 },
]

export const progressSettings: ProgressSettings = { enabled: true, showStreaks: true, showOnLeaderboard: true, timeZone: 'Europe/Amsterdam' }

export const progressSummaryRich: ProgressSummary = {
  enabled: true,
  showStreaks: true,
  level: { level: 12, xp: 4820, intoLevel: 320, levelSpan: 700, nextLevelXp: 5200, progress: 320 / 700 },
  chaptersRead: 1284,
  readingSeconds: 213_000,
  seriesFinished: 9,
  daysRead: 118,
  currentStreak: 6,
  longestStreak: 31,
  earned: 8,
  total: achievements.length,
  recent: achievements.slice(0, 2),
  goals: [
    { id: 1, period: 'Day', metric: 'Chapters', target: 5, progress: 5 },
    { id: 2, period: 'Week', metric: 'Minutes', target: 300, progress: 184 },
    { id: 3, period: 'Year', metric: 'SeriesFinished', target: 24, progress: 9 },
  ],
}

export const progressSummaryNewcomer: ProgressSummary = {
  ...progressSummaryRich,
  level: { level: 1, xp: 40, intoLevel: 40, levelSpan: 100, nextLevelXp: 100, progress: 0.4 },
  chaptersRead: 4,
  readingSeconds: 1500,
  seriesFinished: 0,
  daysRead: 2,
  currentStreak: 2,
  longestStreak: 2,
  earned: 0,
  recent: [],
  goals: [],
}

export const progressSummaryOff: ProgressSummary = { ...progressSummaryRich, enabled: false }

const userBase = { disabled: false, pendingSetup: false, createdAt: daysAgo(400), lastLoginAt: daysAgo(0) }
const toSummary = (me: typeof adminMe, extra: Partial<UserSummary> = {}): UserSummary => {
  const summary: UserSummary & { oidcUserName?: string | null } = { ...me, ...userBase, ...extra }
  delete summary.oidcUserName
  return summary
}
export const users: UserSummary[] = [toSummary(adminMe), toSummary(readerMe, { lastLoginAt: daysAgo(2) })]
