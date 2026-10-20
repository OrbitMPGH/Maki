import { http, HttpResponse } from 'msw'
import {
  DISCOVER_SECTIONS,
  HOME_GLANCE_PANELS,
  HOME_HERO_DEFAULTS,
  HOME_SECTIONS,
  type HealthIssue,
  type MetadataSettings,
  type SetupStatus,
  type UiSettings,
} from '../../src/api/hooks'
import type { QueueSummaryDto } from '../../src/api/types'
import { adminMe, api } from './api'
import { imageHandlers } from './images'

/**
 * The library every area's fixtures draw from, so a series has the same title, cover and creator
 * on Home, in the library, on its own page and in the reader. Titles are invented on purpose.
 * Area files map these onto whatever DTO their endpoint returns.
 */
export interface CatalogueEntry {
  id: number
  title: string
  nativeTitle: string
  author: string
  artist: string
  year: number
  status: 'Ongoing' | 'Completed' | 'Hiatus' | 'Cancelled'
  type: 'Manga' | 'Manhwa' | 'Manhua'
  chapters: number
  read: number
  rating: number
  genres: string[]
  tags: string[]
  synopsis: string
  source: string
  mangaBakaId: number
}

export const catalogue: CatalogueEntry[] = [
  {
    id: 1,
    title: 'Lanterns Over Kasumi',
    nativeTitle: '霞の上の灯籠',
    author: 'Aoi Mizuhara',
    artist: 'Aoi Mizuhara',
    year: 2021,
    status: 'Ongoing',
    type: 'Manga',
    chapters: 142,
    read: 131,
    rating: 8.7,
    genres: ['Fantasy', 'Adventure', 'Drama'],
    tags: ['Found Family', 'Travel', 'Magic'],
    synopsis:
      'A lamplighter who can read the memories caught in flame follows a festival caravan across a valley that forgets itself every winter.',
    source: 'MangaDex',
    mangaBakaId: 10001,
  },
  {
    id: 2,
    title: 'The Ninth Kitchen',
    nativeTitle: '第九の厨房',
    author: 'Haru Takeda',
    artist: 'Mei Okabe',
    year: 2019,
    status: 'Completed',
    type: 'Manga',
    chapters: 96,
    read: 96,
    rating: 8.2,
    genres: ['Slice of Life', 'Comedy'],
    tags: ['Cooking', 'Workplace'],
    synopsis: 'Eight kitchens closed under her. The ninth is a ramen stall in a train station, and it is not closing.',
    source: 'MangaDex',
    mangaBakaId: 10002,
  },
  {
    id: 3,
    title: 'Iron Petal Academy',
    nativeTitle: '철꽃 아카데미',
    author: 'Seo Jiwon',
    artist: 'Kang Min',
    year: 2022,
    status: 'Ongoing',
    type: 'Manhwa',
    chapters: 88,
    read: 40,
    rating: 7.9,
    genres: ['Action', 'School Life', 'Fantasy'],
    tags: ['Mecha', 'Rivals'],
    synopsis: 'The academy trains pilots for machines that bloom. The scholarship student was supposed to be a mechanic.',
    source: 'Asura Scans',
    mangaBakaId: 10003,
  },
  {
    id: 4,
    title: 'Paper Moon Detective',
    nativeTitle: '紙の月探偵',
    author: 'Ren Kurosawa',
    artist: 'Ren Kurosawa',
    year: 2018,
    status: 'Hiatus',
    type: 'Manga',
    chapters: 61,
    read: 61,
    rating: 8.4,
    genres: ['Mystery', 'Supernatural'],
    tags: ['Detective', 'Yokai'],
    synopsis: 'Every case in this town is solved by midnight, because after midnight the witnesses turn back into paper.',
    source: 'Comick',
    mangaBakaId: 10004,
  },
  {
    id: 5,
    title: 'Summer at Shiokaze Station',
    nativeTitle: '潮風駅の夏',
    author: 'Nanami Sato',
    artist: 'Nanami Sato',
    year: 2023,
    status: 'Ongoing',
    type: 'Manga',
    chapters: 34,
    read: 34,
    rating: 8.9,
    genres: ['Romance', 'Slice of Life'],
    tags: ['Seaside', 'Coming of Age'],
    synopsis: 'The last train leaves at 6:40. She keeps missing it, and the stationmaster keeps making tea.',
    source: 'MangaDex',
    mangaBakaId: 10005,
  },
  {
    id: 6,
    title: 'Ashen Crown Regression',
    nativeTitle: '잿빛 왕관의 회귀',
    author: 'Park Haneul',
    artist: 'Studio Lark',
    year: 2020,
    status: 'Ongoing',
    type: 'Manhwa',
    chapters: 203,
    read: 150,
    rating: 7.6,
    genres: ['Action', 'Fantasy'],
    tags: ['Regression', 'Royalty', 'Revenge'],
    synopsis: 'Executed at the coronation, the crown prince wakes up ten years earlier with the executioner as his tutor.',
    source: 'Asura Scans',
    mangaBakaId: 10006,
  },
  {
    id: 7,
    title: 'Clockwork Tea House',
    nativeTitle: '機巧茶館',
    author: 'Lin Yue',
    artist: 'Lin Yue',
    year: 2021,
    status: 'Completed',
    type: 'Manhua',
    chapters: 120,
    read: 12,
    rating: 7.8,
    genres: ['Fantasy', 'Romance'],
    tags: ['Steampunk', 'Tea'],
    synopsis: 'The automaton who serves tea in the old quarter has started remembering customers it never met.',
    source: 'Comick',
    mangaBakaId: 10007,
  },
  {
    id: 8,
    title: 'Bluefin Boys',
    nativeTitle: 'ブルーフィン・ボーイズ',
    author: 'Kenta Ishida',
    artist: 'Kenta Ishida',
    year: 2017,
    status: 'Completed',
    type: 'Manga',
    chapters: 210,
    read: 0,
    rating: 8.0,
    genres: ['Sports', 'Comedy'],
    tags: ['Swimming', 'Team'],
    synopsis: 'A swim club with four members, one pool and a coach who cannot swim.',
    source: 'MangaDex',
    mangaBakaId: 10008,
  },
  {
    id: 9,
    title: 'Grave Robber of the Seventh Hall',
    nativeTitle: '第七殿の墓荒らし',
    author: 'Yuu Amano',
    artist: 'Daichi Fujii',
    year: 2024,
    status: 'Ongoing',
    type: 'Manga',
    chapters: 22,
    read: 18,
    rating: 8.1,
    genres: ['Horror', 'Adventure'],
    tags: ['Dungeon', 'Dark Fantasy'],
    synopsis: 'The halls below the capital are full of dead heroes and their gear. Someone has to bring it back up.',
    source: 'Weeb Central',
    mangaBakaId: 10009,
  },
  {
    id: 10,
    title: 'Snowfield Letters',
    nativeTitle: '雪原の手紙',
    author: 'Aoi Mizuhara',
    artist: 'Aoi Mizuhara',
    year: 2016,
    status: 'Completed',
    type: 'Manga',
    chapters: 48,
    read: 48,
    rating: 9.1,
    genres: ['Drama', 'Romance'],
    tags: ['Letters', 'Historical'],
    synopsis: 'A postman in the far north delivers one letter a year to a house that has been empty since the war.',
    source: 'MangaDex',
    mangaBakaId: 10010,
  },
  {
    id: 11,
    title: 'Neon Shrine Maiden',
    nativeTitle: 'ネオン巫女',
    author: 'Sora Hino',
    artist: 'Sora Hino',
    year: 2022,
    status: 'Ongoing',
    type: 'Manga',
    chapters: 57,
    read: 20,
    rating: 7.4,
    genres: ['Sci-Fi', 'Supernatural', 'Action'],
    tags: ['Cyberpunk', 'Shrine'],
    synopsis: 'Gods moved into the city network. The shrine maiden now does her exorcisms with a soldering iron.',
    source: 'Comick',
    mangaBakaId: 10011,
  },
  {
    id: 12,
    title: 'Under the Willow Gate',
    nativeTitle: '柳门之下',
    author: 'Zhou Wen',
    artist: 'Qing Studio',
    year: 2019,
    status: 'Cancelled',
    type: 'Manhua',
    chapters: 75,
    read: 75,
    rating: 6.9,
    genres: ['Historical', 'Martial Arts'],
    tags: ['Wuxia', 'Sect'],
    synopsis: 'The gatekeeper of a sect nobody joins anymore takes one last disciple.',
    source: 'Comick',
    mangaBakaId: 10012,
  },
]

export const seriesById = (id: number) => catalogue.find((s) => s.id === id) ?? catalogue[0]

export const setupStatus: SetupStatus = { completed: true }

export const uiSettings: UiSettings = {
  startPage: 'home',
  homeLayout: {
    enabled: true,
    sections: HOME_SECTIONS.map((key) => ({
      key,
      enabled: true,
      hero: key in HOME_HERO_DEFAULTS ? HOME_HERO_DEFAULTS[key] : null,
      panels: key === 'glance' ? HOME_GLANCE_PANELS.map((p) => ({ key: p, enabled: true })) : null,
    })),
  },
  seriesSections: { related: true, similar: true },
  titleLanguage: '',
  language: 'en',
  discoverLayout: { sections: DISCOVER_SECTIONS.map((key) => ({ key, enabled: true })) },
}

export const metadataSettings: MetadataSettings = {
  useLocalDb: true,
  dumpPresent: true,
  dumpSizeBytes: 1_842_000_000,
  dumpRefreshedAt: '2026-10-12T03:00:00Z',
}

export const healthIssues: HealthIssue[] = []

export const queueSummary: QueueSummaryDto = { active: 2, awaitingImport: 0, failed: 1 }

function titleFor(seed: string): string {
  const id = Number(seed)
  return Number.isInteger(id) && catalogue.some((s) => s.id === id) ? seriesById(id).title : seed
}

export const sharedHandlers = [
  http.get('/initialize.json', () =>
    HttpResponse.json({
      apiRoot: '/api/v1',
      setupNeeded: false,
      oidc: { enabled: true, displayName: 'Authentik', localLoginRestricted: false },
    }),
  ),
  http.get(api('/auth/me'), () => HttpResponse.json(adminMe)),
  http.get(api('/settings/setup'), () => HttpResponse.json(setupStatus)),
  http.get(api('/settings/ui'), () => HttpResponse.json(uiSettings)),
  http.get(api('/settings/metadata'), () => HttpResponse.json(metadataSettings)),
  http.get(api('/system/health'), () => HttpResponse.json(healthIssues)),
  http.get(api('/queue/summary'), () => HttpResponse.json(queueSummary)),
  http.get(api('/inbox/unread-count'), () => HttpResponse.json({ count: 3 })),
  http.get(api('/requests/pendingcount'), () => HttpResponse.json({ count: 2 })),
  ...imageHandlers(titleFor),
]
