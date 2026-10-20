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

// Deterministic per seed, so a series keeps its colour everywhere it appears.
// Golden-angle steps spread consecutive ids (1, 2, 3...) across the wheel instead of clustering.
function hue(seed: string): number {
  let h = 0
  for (const c of seed) h = (h * 31 + c.charCodeAt(0)) >>> 0
  return Math.round((h * 137.508) % 360)
}

function titleFor(seed: string): string {
  const id = Number(seed)
  return Number.isInteger(id) && catalogue.some((s) => s.id === id) ? seriesById(id).title : seed
}

function escapeXml(s: string): string {
  return s.replace(/[<>&"']/g, (c) => `&#${c.charCodeAt(0)};`)
}

function svg(body: string, w: number, h: number) {
  return new HttpResponse(
    `<svg xmlns="http://www.w3.org/2000/svg" width="${w}" height="${h}" viewBox="0 0 ${w} ${h}">${body}</svg>`,
    { headers: { 'Content-Type': 'image/svg+xml' } },
  )
}

// Abstract art only: every surface that shows a cover already prints the title beside or over it.
function coverSvg(seed: string) {
  const h = hue(seed)
  const n = h % 3
  const figure =
    n === 0
      ? `<circle cx="210" cy="140" r="78" fill="hsl(${(h + 180) % 360},70%,72%)" opacity=".45"/>`
      : n === 1
        ? `<path d="M0 330 L120 210 L200 280 L300 170 L300 450 L0 450 Z" fill="hsl(${(h + 30) % 360},45%,16%)" opacity=".7"/><circle cx="80" cy="110" r="36" fill="hsl(${(h + 60) % 360},80%,80%)" opacity=".6"/>`
        : `<rect x="40" y="60" width="220" height="220" rx="110" fill="none" stroke="hsl(${(h + 160) % 360},70%,75%)" stroke-width="14" opacity=".5"/>`
  return svg(
    `<defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="hsl(${h},55%,50%)"/><stop offset="1" stop-color="hsl(${(h + 50) % 360},60%,20%)"/></linearGradient></defs>` +
      `<rect width="300" height="450" fill="url(#g)"/>${figure}<rect y="330" width="300" height="120" fill="#000" opacity=".25"/>`,
    300,
    450,
  )
}

function pageSvg(seed: string, n: string) {
  const h = hue(seed)
  const panels = [
    [30, 30, 740, 360],
    [30, 410, 360, 420],
    [410, 410, 360, 420],
    [30, 850, 740, 320],
  ]
    .map(([x, y, w, ht], i) => `<rect x="${x}" y="${y}" width="${w}" height="${ht}" fill="hsl(${(h + i * 25) % 360},25%,${88 - i * 6}%)" stroke="#111" stroke-width="4"/>`)
    .join('')
  return svg(
    `<rect width="800" height="1200" fill="#fafafa"/>${panels}<text x="400" y="1185" font-family="sans-serif" font-size="22" text-anchor="middle" fill="#555">${escapeXml(titleFor(seed))} - ${escapeXml(n)}</text>`,
    800,
    1200,
  )
}

function spreadSvg(seed: string, n: string) {
  const h = hue(seed)
  const half = (x: number, label: string, i: number) =>
    `<rect x="${x + 30}" y="30" width="740" height="1140" fill="hsl(${(h + i * 40) % 360},30%,82%)" stroke="#111" stroke-width="4"/>` +
    `<text x="${x + 400}" y="620" font-family="sans-serif" font-size="64" text-anchor="middle" fill="#222">${label}</text>`
  return svg(
    `<rect width="1600" height="1200" fill="#fafafa"/>${half(800, 'Read first', 0)}${half(0, 'Read second', 1)}<text x="800" y="1185" font-family="sans-serif" font-size="22" text-anchor="middle" fill="#555">${escapeXml(titleFor(seed))} - ${escapeXml(n)}</text>`,
    1600,
    1200,
  )
}

function stripSvg(seed: string, n: string) {
  const h = hue(seed)
  return svg(
    `<rect width="800" height="2400" fill="hsl(${h},20%,92%)"/><rect x="60" y="200" width="680" height="900" fill="hsl(${h},35%,70%)"/><rect x="60" y="1300" width="680" height="900" fill="hsl(${(h + 30) % 360},35%,60%)"/><text x="400" y="2360" font-family="sans-serif" font-size="28" text-anchor="middle" fill="#333">${escapeXml(titleFor(seed))} - ${escapeXml(n)}</text>`,
    800,
    2400,
  )
}

function bannerSvg(seed: string) {
  const h = hue(seed)
  return svg(
    `<defs><linearGradient id="b" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="hsl(${h},50%,30%)"/><stop offset="1" stop-color="hsl(${(h + 70) % 360},55%,18%)"/></linearGradient></defs><rect width="1600" height="600" fill="url(#b)"/>`,
    1600,
    600,
  )
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
  // The real cover route, for any fixture that carries a server-shaped coverUrl.
  http.get(api('/mediacover/:id/:file'), ({ params }) => coverSvg(String(params.id))),
  http.get('/mock/cover/:seed', ({ params }) => coverSvg(String(params.seed).replace(/\.svg$/, ''))),
  http.get('/mock/page/:seed/:n', ({ params }) => pageSvg(String(params.seed), String(params.n).replace(/\.svg$/, ''))),
  http.get('/mock/spread/:seed/:n', ({ params }) => spreadSvg(String(params.seed), String(params.n).replace(/\.svg$/, ''))),
  http.get('/mock/strip/:seed/:n', ({ params }) => stripSvg(String(params.seed), String(params.n).replace(/\.svg$/, ''))),
  http.get('/mock/banner/:seed', ({ params }) => bannerSvg(String(params.seed).replace(/\.svg$/, ''))),
  http.get(api('/reader/chapter/:id/page/:n'), ({ params }) => pageSvg(String(params.id), String(params.n))),
  http.get(api('/reader/chapter/:id/thumb/:n'), ({ params }) => pageSvg(String(params.id), String(params.n))),
]
