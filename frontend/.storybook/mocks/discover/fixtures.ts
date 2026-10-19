import { READER_COHORT_FEED } from '../../../src/api/hooks'
import type {
  BehaviourSeries,
  DiscoverPreset,
  DiscoverRail,
  HiddenContent,
  MangaBakaDetail,
  MangaReview,
  ReadingBehaviour,
  RecommendationDefaults,
  RecommendationIndexStatus,
  RecommendationItem,
  RecommendationsResult,
  SearchDefaults,
  TagOption,
  TasteFacet,
  TasteGroup,
  TasteInsights,
  TasteMember,
  TasteProfile,
} from '../../../src/api/hooks'
import type { AnimeSignalEntry, AnimeSignalsData } from '../../../src/api/animeSignals'
import type {
  FeedbackActivity,
  FeedbackLabData,
  FeedbackState,
  SignalOverrideState,
} from '../../../src/api/recommendationFeedback'
import type { SeriesPreview } from '../../../src/api/preview'
import type { FollowedCreators } from '../../../src/api/following'
import { cover, daysAgo, hoursAgo } from '../api'
import { catalogue } from '../shared'

interface Seed {
  title: string
  native?: string
  genres: string[]
  tags: string[]
  author: string
  artist?: string
  type: 'Manga' | 'Manhwa' | 'Manhua'
  status: 'Ongoing' | 'Completed' | 'Hiatus' | 'Cancelled'
  year: number
  rating: number
  chapters: number
  blurb: string
}

/** Invented catalogue entries that are not in the library, so Discover has something to offer. */
const SEEDS: Seed[] = [
  { title: 'The Salt Cartographer', genres: ['Adventure', 'Fantasy'], tags: ['Travel', 'Found Family'], author: 'Mio Tachibana', type: 'Manga', status: 'Ongoing', year: 2023, rating: 84, chapters: 61, blurb: 'A mapmaker charts a coastline that rearranges itself every tide, and sells the maps to the people who live on it.' },
  { title: 'Midnight Laundromat', genres: ['Slice of Life', 'Supernatural'], tags: ['Workplace', 'Ghosts'], author: 'Eri Kondo', type: 'Manga', status: 'Completed', year: 2020, rating: 81, chapters: 88, blurb: 'The only shop open after midnight takes in clothes that belong to people who are no longer around.' },
  { title: 'Crown of Ash and Rain', genres: ['Fantasy', 'Action'], tags: ['Royalty', 'Revenge'], author: 'Han Yeojin', artist: 'Studio Moru', type: 'Manhwa', status: 'Ongoing', year: 2022, rating: 79, chapters: 140, blurb: 'A disgraced duchess claws her way back through a court that has already buried her name.' },
  { title: 'Seventh Seat Orchestra', genres: ['Drama', 'Slice of Life'], tags: ['Music', 'Rivals'], author: 'Kaede Morishita', type: 'Manga', status: 'Ongoing', year: 2021, rating: 86, chapters: 97, blurb: 'Six chairs are filled. The seventh belongs to whoever can make the conductor stop counting.' },
  { title: 'Paper Lantern Express', genres: ['Mystery', 'Adventure'], tags: ['Train', 'Detective'], author: 'Ryo Nakagawa', type: 'Manga', status: 'Completed', year: 2017, rating: 83, chapters: 54, blurb: 'Every car on the night train holds one more suspect than it has seats.' },
  { title: 'Tidewater Knights', genres: ['Action', 'Fantasy'], tags: ['Knights', 'Ocean'], author: 'Seo Daeun', artist: 'Kim Harin', type: 'Manhwa', status: 'Ongoing', year: 2024, rating: 76, chapters: 38, blurb: 'The sea retreats once a decade. This time the order that guards the seabed is not ready.' },
  { title: 'Hundred Day Garden', genres: ['Slice of Life', 'Romance'], tags: ['Gardening', 'Coming of Age'], author: 'Yui Hasegawa', type: 'Manga', status: 'Completed', year: 2019, rating: 88, chapters: 36, blurb: 'She has a hundred days to bring a dead greenhouse back before the inspector returns.' },
  { title: 'Foxglove Protocol', genres: ['Sci-Fi', 'Thriller'], tags: ['Cyberpunk', 'Conspiracy'], author: 'Takumi Aoyama', type: 'Manga', status: 'Hiatus', year: 2018, rating: 77, chapters: 72, blurb: 'A pharmacist in a flooded megacity finds a patch of medicine that no one admits to making.' },
  { title: 'Little Dragon Bakery', genres: ['Comedy', 'Fantasy'], tags: ['Cooking', 'Dragons'], author: 'Nina Fukuda', type: 'Manga', status: 'Ongoing', year: 2023, rating: 80, chapters: 49, blurb: 'The dragon in the basement does not breathe fire. It proofs dough.' },
  { title: 'Ink Tide Academy', genres: ['School Life', 'Supernatural'], tags: ['Magic', 'Rivals'], author: 'Zhang Rui', type: 'Manhua', status: 'Ongoing', year: 2022, rating: 74, chapters: 180, blurb: 'Calligraphy students whose strokes come alive have to learn which characters not to write.' },
  { title: 'Lighthouse Without a Sea', genres: ['Drama', 'Mystery'], tags: ['Small Town', 'Letters'], author: 'Haruka Ono', type: 'Manga', status: 'Completed', year: 2015, rating: 90, chapters: 42, blurb: 'The keeper tends the lamp every night in a town the water left behind forty years ago.' },
  { title: 'Reborn as the Villain\'s Accountant', genres: ['Fantasy', 'Comedy'], tags: ['Regression', 'Workplace'], author: 'Lee Soyun', artist: 'Studio Lark', type: 'Manhwa', status: 'Ongoing', year: 2024, rating: 72, chapters: 66, blurb: 'The dark lord is magnificent at conquest and terrible at bookkeeping. Someone has to fix that.' },
  { title: 'Orchard of Small Gods', genres: ['Fantasy', 'Slice of Life'], tags: ['Folklore', 'Seasons'], author: 'Aoi Shirakawa', type: 'Manga', status: 'Ongoing', year: 2021, rating: 85, chapters: 74, blurb: 'Each tree in the old orchard has a god small enough to fit in a teacup, and most of them are in a mood.' },
  { title: 'Cold Open', genres: ['Drama', 'Comedy'], tags: ['Showbiz', 'Workplace'], author: 'Mina Park', type: 'Manhwa', status: 'Completed', year: 2020, rating: 78, chapters: 110, blurb: 'The night shift of a failing radio drama keeps writing itself into the script.' },
  { title: 'Glass Harbor Nocturne', genres: ['Mystery', 'Romance'], tags: ['Detective', 'Seaside'], author: 'Rei Takahashi', type: 'Manga', status: 'Ongoing', year: 2025, rating: 82, chapters: 14, blurb: 'A harbour pianist and a customs inspector are the only ones awake when the cargo goes missing.' },
  { title: 'Eleven Thousand Steps', genres: ['Adventure', 'Drama'], tags: ['Pilgrimage', 'Mountains'], author: 'Sun Mei', type: 'Manhua', status: 'Completed', year: 2016, rating: 87, chapters: 92, blurb: 'The monastery is at the top of the mountain, and the climb takes exactly as long as you are sorry.' },
  { title: 'Cinder Class Heroes', genres: ['Action', 'School Life'], tags: ['Rivals', 'Superpowers'], author: 'Daichi Mori', type: 'Manga', status: 'Ongoing', year: 2022, rating: 73, chapters: 126, blurb: 'The hero course only ever had one empty seat, and the transfer student is sitting in it.' },
  { title: 'The Quiet Switchboard', genres: ['Mystery', 'Supernatural'], tags: ['Ghosts', 'Historical'], author: 'Miyu Arai', type: 'Manga', status: 'Cancelled', year: 2014, rating: 75, chapters: 29, blurb: 'Operators at the last manual exchange keep connecting calls to numbers that were disconnected decades ago.' },
  { title: 'Spice Road Apprentice', genres: ['Adventure', 'Comedy'], tags: ['Cooking', 'Travel'], author: 'Omar Haddad', artist: 'Lila Voss', type: 'Manga', status: 'Ongoing', year: 2023, rating: 79, chapters: 52, blurb: 'A cook who cannot taste anything signs on with a caravan of people who can taste far too much.' },
  { title: 'Moonlit Debt Collector', genres: ['Supernatural', 'Action'], tags: ['Yokai', 'Dark Fantasy'], author: 'Ken Ishikawa', type: 'Manga', status: 'Ongoing', year: 2024, rating: 80, chapters: 31, blurb: 'The ledger lists every favour a spirit ever owed, and he is the only one who can read it.' },
  { title: 'Rooftop Radio Club', genres: ['Slice of Life', 'Comedy'], tags: ['School', 'Music'], author: 'Saki Nomura', type: 'Manga', status: 'Completed', year: 2018, rating: 82, chapters: 58, blurb: 'Three students broadcast from the school roof to a town that mostly cannot hear them.' },
  { title: 'Wolfsbane Court', genres: ['Fantasy', 'Romance'], tags: ['Royalty', 'Werewolves'], author: 'Cho Eunji', artist: 'Studio Moru', type: 'Manhwa', status: 'Ongoing', year: 2023, rating: 77, chapters: 105, blurb: 'A betrothal between two rival packs is held together by a hostage nobody told the truth.' },
  { title: 'Tenth Floor Detective Agency', genres: ['Mystery', 'Comedy'], tags: ['Detective', 'Workplace'], author: 'Goro Yamashita', type: 'Manga', status: 'Ongoing', year: 2020, rating: 81, chapters: 102, blurb: 'The elevator only stops at ten when there is a case. Business has been very good.' },
  { title: 'A Kingdom of Thirty Chairs', genres: ['Drama', 'Historical'], tags: ['Politics', 'Royalty'], author: 'Liang Chen', type: 'Manhua', status: 'Completed', year: 2019, rating: 85, chapters: 150, blurb: 'Thirty councillors, one throne, and a regent who has to outlive all of them.' },
  { title: 'Static Between Stars', genres: ['Sci-Fi', 'Drama'], tags: ['Space', 'Letters'], author: 'Noa Berg', type: 'Manga', status: 'Ongoing', year: 2025, rating: 83, chapters: 18, blurb: 'The relay station receives one transmission a year. This year it is addressed to her.' },
  { title: 'Winter Cram School', genres: ['School Life', 'Drama'], tags: ['Exams', 'Rivals'], author: 'Yuka Sasaki', type: 'Manga', status: 'Completed', year: 2016, rating: 79, chapters: 66, blurb: 'Forty students, one seat at the top university, and a snowstorm that closes the roads.' },
  { title: 'The Bell Ringer of Low Town', genres: ['Fantasy', 'Mystery'], tags: ['Dark Fantasy', 'Small Town'], author: 'Fang Jun', type: 'Manhua', status: 'Ongoing', year: 2021, rating: 76, chapters: 158, blurb: 'The bells ring for the dead before they die. Lately they have been ringing for the wrong people.' },
  { title: 'Aquarium After Hours', genres: ['Romance', 'Slice of Life'], tags: ['Seaside', 'Workplace'], author: 'Hina Kuroda', type: 'Manga', status: 'Ongoing', year: 2024, rating: 84, chapters: 27, blurb: 'The night cleaner and the aquarist share a shift change, a bench, and nothing else, for now.' },
  { title: 'Rustwind Rangers', genres: ['Action', 'Sci-Fi'], tags: ['Mecha', 'Post-Apocalyptic'], author: 'Jae Hoon', artist: 'Kang Min', type: 'Manhwa', status: 'Hiatus', year: 2019, rating: 71, chapters: 120, blurb: 'Four rangers patrol a desert of scrap with one working machine between them.' },
  { title: 'The Long Intermission', genres: ['Drama', 'Comedy'], tags: ['Showbiz', 'Found Family'], author: 'Asuka Maeda', type: 'Manga', status: 'Completed', year: 2017, rating: 80, chapters: 80, blurb: 'A theatre company keeps the curtain down for one more act while the rest of the world waits.' },
  { title: 'A Very Long Title That Keeps Going Past Any Reasonable Cover Width, Volume the Complete Chronicle', genres: ['Fantasy', 'Adventure', 'Comedy', 'Drama'], tags: ['Travel', 'Found Family', 'Magic', 'Folklore', 'Seasons'], author: 'Takahiro Nishimura-Whitfield', type: 'Manga', status: 'Ongoing', year: 2022, rating: 69, chapters: 33, blurb: 'Included to check how a title of absurd length and a card with many tags holds its shape.' },
]

const BASE_ID = 30000

/** One catalogue card, built the way the server hydrates it from the dump. */
export function catalogueItem(n: number, overrides: Partial<RecommendationItem> = {}): RecommendationItem {
  const s = SEEDS[n % SEEDS.length]
  const art = cover(s.title)
  return {
    providerId: String(BASE_ID + (n % SEEDS.length)),
    title: s.title,
    coverUrl: art,
    thumbUrl: art,
    thumbUrlHiDpi: art,
    year: s.year,
    description: s.blurb,
    status: s.status,
    rating: s.rating,
    totalChapters: s.chapters,
    matchedGenres: s.genres,
    matchedTags: s.tags.slice(0, 3),
    authorMatch: false,
    relationKind: null,
    relatedToTitle: null,
    becauseOfTitle: null,
    ...overrides,
  }
}

/** A catalogue card for a series that is already in the library, matched by MangaBaka id. */
export function ownedItem(seriesId: number, overrides: Partial<RecommendationItem> = {}): RecommendationItem {
  const e = catalogue.find((c) => c.id === seriesId) ?? catalogue[0]
  const art = cover(e.id)
  return {
    providerId: String(e.mangaBakaId),
    title: e.title,
    coverUrl: art,
    thumbUrl: art,
    thumbUrlHiDpi: art,
    year: e.year,
    description: e.synopsis,
    status: e.status,
    rating: Math.round(e.rating * 10),
    totalChapters: e.chapters,
    matchedGenres: e.genres,
    matchedTags: e.tags,
    authorMatch: false,
    relationKind: null,
    relatedToTitle: null,
    becauseOfTitle: null,
    ...overrides,
  }
}

const range = (from: number, count: number) => Array.from({ length: count }, (_, i) => from + i)
const items = (from: number, count: number) => range(from, count).map((n) => catalogueItem(n))

const rail = (key: string, title: string, feed: string, list: RecommendationItem[], extra: Partial<DiscoverRail> = {}): DiscoverRail => ({
  key,
  title,
  feed,
  genre: null,
  items: list,
  subtitle: null,
  ...extra,
})

export const trendingRail = rail('trending', 'Trending this week', 'Trending', [
  ...items(0, 5),
  ownedItem(5),
  ...items(5, 14),
])

export const catalogueRails: DiscoverRail[] = [
  rail('popular', 'Most popular', 'Popular', [ownedItem(10), ...items(8, 16), ownedItem(2), ...items(0, 4)]),
  rail('top-rated', 'Top rated', 'TopRated', items(10, 20)),
  rail('new', 'Newly released', 'New', items(14, 18)),
  rail('manhwa', 'Manhwa', 'Manhwa', items(2, 16)),
  rail('manhua', 'Manhua', 'Manhua', items(9, 12)),
]

export const discoverRails: DiscoverRail[] = [trendingRail, ...catalogueRails]

const SEED_IDS = [1, 6, 5].map((id) => catalogue.find((c) => c.id === id)!.mangaBakaId)

export const recentRail: DiscoverRail = rail(
  'recent',
  'Because of what you read lately',
  'Recent',
  [
    catalogueItem(0, { becauseOfTitle: catalogue[0].title }),
    catalogueItem(12, { becauseOfTitle: catalogue[0].title, coRead: true, coReadWithTitle: catalogue[5].title }),
    catalogueItem(3, { becauseOfTitle: catalogue[5].title, authorMatch: true }),
    catalogueItem(6, { becauseOfTitle: catalogue[4].title, tasteMatch: true }),
    catalogueItem(14, { relationKind: 'Sequel', relatedToTitle: catalogue[4].title }),
    catalogueItem(20, { becauseOfTitle: catalogue[0].title }),
    catalogueItem(1),
    catalogueItem(22),
    catalogueItem(17),
  ],
  { seedIds: SEED_IDS, subtitle: `Because you read ${catalogue[0].title}, ${catalogue[5].title} and 1 more` },
)

export const sideInterestRails: DiscoverRail[] = [
  rail('side-cooking', 'A taste for cooking stories', 'SideInterest', [catalogueItem(8), catalogueItem(19), catalogueItem(1), catalogueItem(13)], {
    seedIds: [catalogue[1].mangaBakaId],
    filters: { tags: ['Cooking'] },
  }),
  rail('side-mystery', 'Mysteries with a supernatural edge', 'SideInterest', [catalogueItem(4), catalogueItem(17), catalogueItem(14), catalogueItem(23), catalogueItem(20)], {
    seedIds: [catalogue[3].mangaBakaId],
    filters: { genres: ['Mystery'] },
  }),
]

export const cohortRail: DiscoverRail = rail(
  'cohort',
  'Readers like you also finished',
  READER_COHORT_FEED,
  [catalogueItem(10), catalogueItem(16), catalogueItem(24), catalogueItem(6), catalogueItem(25), catalogueItem(15)],
  { subtitle: 'Finished by readers whose libraries overlap yours' },
)

const GENRES = ['Action', 'Adventure', 'Comedy', 'Drama', 'Fantasy', 'Mystery', 'Romance', 'Sci-Fi', 'Slice of Life', 'Supernatural']

export const genreRails: DiscoverRail[] = GENRES.map((g, i) =>
  rail(`genre-${g.toLowerCase().replace(/\s+/g, '-')}`, `Popular in ${g}`, 'Popular', items(i * 2, 12), { genre: g }),
)

export const followedCreators: FollowedCreators = {
  creators: [
    { name: 'Aoi Mizuhara', role: 'author' },
    { name: 'Studio Lark', role: 'studio' },
    { name: 'Mio Tachibana' },
  ],
}

export const followingItems: RecommendationItem[] = [catalogueItem(0), catalogueItem(11), catalogueItem(2), catalogueItem(18), catalogueItem(14)]

const detailExtras: Record<string, Partial<MangaBakaDetail>> = {}

export function detailFor(providerId: string): MangaBakaDetail {
  const owned = catalogue.find((c) => String(c.mangaBakaId) === providerId)
  const seedIndex = Number(providerId) - BASE_ID
  const s = SEEDS[seedIndex] ?? SEEDS[0]
  const base: MangaBakaDetail = owned
    ? {
        providerId,
        title: owned.title,
        nativeTitle: owned.nativeTitle,
        romanizedTitle: null,
        altTitles: [],
        description: owned.synopsis,
        coverUrl: cover(owned.id),
        year: owned.year,
        type: owned.type.toLowerCase(),
        status: owned.status,
        contentRating: 'safe',
        rating: Math.round(owned.rating * 10),
        sourceRatings: [{ source: 'MyAnimeList', rating: Math.round(owned.rating * 10) - 2 }],
        totalChapters: owned.chapters,
        finalVolume: owned.status === 'Completed' ? Math.ceil(owned.chapters / 9) : null,
        authors: [owned.author],
        artists: owned.artist === owned.author ? [] : [owned.artist],
        publishers: ['Hoshimori Press'],
        genres: owned.genres,
        tags: owned.tags.map((name, i) => ({ name, weight: i === 0 ? 'core' : 'defining', description: null, isSpoiler: false })),
        links: [{ site: 'mangabaka', url: `https://mangabaka.example/series/${providerId}` }],
        malId: null,
        hasAnime: false,
        animeStart: null,
        animeEnd: null,
        readerHint: null,
        animeResume: null,
      }
    : {
        providerId,
        title: s.title,
        nativeTitle: null,
        romanizedTitle: null,
        altTitles: [{ title: `${s.title} (Alternate)`, language: 'en' }],
        description: `${s.blurb}\n\nThe catalogue entry runs a little longer than the card blurb, so the synopsis block has more than a single line to show and the Show more control has something to fold away.`,
        coverUrl: cover(s.title),
        year: s.year,
        type: s.type.toLowerCase(),
        status: s.status,
        contentRating: 'safe',
        rating: s.rating,
        sourceRatings: [
          { source: 'MyAnimeList', rating: s.rating - 3 },
          { source: 'AniList', rating: s.rating + 1 },
        ],
        totalChapters: s.chapters,
        finalVolume: s.status === 'Completed' ? Math.ceil(s.chapters / 9) : null,
        authors: [s.author],
        artists: s.artist ? [s.artist] : [],
        publishers: ['Hoshimori Press', 'Lantern Comics'],
        genres: s.genres,
        tags: [
          ...s.tags.map((name, i) => ({ name, weight: i === 0 ? 'core' : 'defining', description: null, isSpoiler: false })),
          { name: 'Unreliable Narrator', weight: 'recurrent', description: 'The viewpoint character gets key facts wrong.', isSpoiler: true },
          { name: 'Slow Burn', weight: 'incidental', description: null, isSpoiler: false },
        ],
        links: [
          { site: 'mangabaka', url: `https://mangabaka.example/series/${providerId}` },
          { site: 'myanimelist', url: 'https://myanimelist.example/manga/1' },
        ],
        malId: 5000 + seedIndex,
        hasAnime: false,
        animeStart: null,
        animeEnd: null,
        readerHint: null,
        animeResume: null,
      }
  return { ...base, ...detailExtras[providerId] }
}

/** The first card of the rich variants: one with an anime, one with a reader-cohort hint. */
export const ANIME_PROVIDER_ID = String(BASE_ID + 6)
export const HINT_PROVIDER_ID = String(BASE_ID + 3)
export const NO_REVIEWS_PROVIDER_ID = String(BASE_ID + 10)
export const NO_SYNOPSIS_PROVIDER_ID = String(BASE_ID + 14)

detailExtras[ANIME_PROVIDER_ID] = {
  hasAnime: true,
  animeStart: 'Vol 1, Chap 1 (S1)',
  animeEnd: 'Vol 4, Chap 22 (S1) / Vol 5, Chap 31 (Film)',
  animeResume: {
    animeTitle: 'Hundred Day Garden (TV)',
    services: ['AniList', 'MyAnimeList'],
    score: 8.4,
    basis: 'allSeasons',
    coveredTo: 22,
    resumeAt: 23,
    coveredLabel: 'Ch. 22',
    nextLabel: 'Ch. 23',
    nextFrom: 23,
    nextTo: 36,
    inLibrarySeriesId: null,
  },
}
detailExtras[HINT_PROVIDER_ID] = { readerHint: { score: 91, baseline: 84, readers: 412 } }
detailExtras[NO_SYNOPSIS_PROVIDER_ID] = {
  description: null,
  tags: [],
  authors: [],
  artists: [],
  publishers: [],
  links: [],
  sourceRatings: [],
  malId: null,
}

export const reviews: MangaReview[] = [
  { author: 'quietpageturner', score: 9, tags: ['Recommended'], date: daysAgo(40), url: 'https://myanimelist.example/review/1', text: 'The pacing is unhurried but it never wastes a chapter. By the halfway mark every side character has earned a scene, and the ending lands because of them.\n\nThe art is clean and the panel flow is easy to follow, even in the busy sequences.' },
  { author: 'nightowl_reads', score: 7, tags: ['Mixed feelings'], date: daysAgo(120), url: null, text: 'Lovely first third, then a long stretch that circles the same ideas. I stayed for the last arc and was glad I did.' },
  { author: 'mapmaker99', score: 10, tags: ['Preliminary'], date: daysAgo(9), url: 'https://myanimelist.example/review/3', text: 'Short and to the point: read it.' },
]

export const tagOptions: TagOption[] = [
  { name: 'Found Family', path: '', count: 812, hasSubtags: false },
  { name: 'School', path: 'Locations', count: 2210, hasSubtags: true },
  { name: 'College', path: 'Locations > School', count: 310, hasSubtags: false },
  { name: 'Workplace', path: 'Locations', count: 640, hasSubtags: false },
  { name: 'Cooking', path: 'Themes', count: 930, hasSubtags: false },
  { name: 'Music', path: 'Themes', count: 760, hasSubtags: false },
  { name: 'Detective', path: 'Themes', count: 540, hasSubtags: false },
  { name: 'Royalty', path: 'Themes', count: 480, hasSubtags: false },
  { name: 'Regression', path: 'Plot', count: 420, hasSubtags: false },
  { name: 'Revenge', path: 'Plot', count: 390, hasSubtags: false },
  { name: 'Time Loop', path: 'Plot', count: 160, hasSubtags: false },
  { name: 'Travel', path: 'Themes', count: 700, hasSubtags: false },
  { name: 'Magic', path: 'Themes', count: 1900, hasSubtags: true },
  { name: 'Folklore', path: 'Themes', count: 280, hasSubtags: false },
  { name: 'Rivals', path: 'Relationships', count: 1100, hasSubtags: false },
  { name: 'Yokai', path: 'Themes', count: 240, hasSubtags: false },
  { name: 'Cyberpunk', path: 'Setting', count: 180, hasSubtags: false },
  { name: 'Seaside', path: 'Locations', count: 150, hasSubtags: false },
  { name: 'Coming of Age', path: 'Themes', count: 1200, hasSubtags: false },
  { name: 'Mecha', path: 'Themes', count: 330, hasSubtags: false },
]

export const hiddenContent: HiddenContent = {
  terms: [
    { kind: 'tag', name: 'Gore' },
    { kind: 'genre', name: 'Horror' },
  ],
}

export const searchDefaults: SearchDefaults = {
  yearMin: null,
  yearMax: null,
  types: null,
  statuses: null,
  genres: null,
  tags: null,
  minChapters: null,
  maxChapters: null,
  minRating: null,
  contentRatings: null,
  rules: null,
  credits: null,
}

export const emptyRecommendationDefaults: RecommendationDefaults = { obscurity: 0, diversity: 0 }

export const savedRecommendationDefaults: RecommendationDefaults = {
  seeds: [{ id: catalogue[0].mangaBakaId, title: catalogue[0].title }],
  yearMin: 2015,
  types: ['manga'],
  minRating: 70,
  obscurity: 0.5,
  diversity: 0.3,
}

export const presets: DiscoverPreset[] = [
  { id: 1, name: 'Short and finished', spec: { statuses: ['completed'], maxChapters: 60 }, sortOrder: 0 },
  { id: 2, name: 'Cozy slice of life', spec: { genres: ['Slice of Life'], minRating: 75 }, sortOrder: 1 },
]

export const recommendedPicks: RecommendationItem[] = [
  catalogueItem(0, { relationKind: 'Sequel', relatedToTitle: catalogue[4].title }),
  catalogueItem(3, { authorMatch: true }),
  catalogueItem(6, { coRead: true, coReadWithTitle: catalogue[1].title }),
  catalogueItem(10, { tasteMatch: true }),
  catalogueItem(12),
  catalogueItem(14),
  ownedItem(9),
  catalogueItem(18, { becauseOfTitle: catalogue[2].title }),
  catalogueItem(21),
  catalogueItem(24),
  catalogueItem(26),
  catalogueItem(28),
]

export function recommendationsPage(page: number): RecommendationsResult {
  const related = page === 0 ? recommendedPicks.slice(0, 3) : []
  const similar = page === 0 ? recommendedPicks.slice(3, 9) : recommendedPicks.slice(9)
  return {
    related,
    similar,
    generatedAt: hoursAgo(2),
    page,
    hasMore: page === 0,
    poolVersion: 'story-pool',
  }
}

const member = (id: number): TasteMember => ({ seriesId: id, title: catalogue[id - 1].title, coverUrl: cover(id) })

const facet = (name: string, weight: number, share: number, support: number, shelf: number | null, cat: number | null): TasteFacet => ({
  name,
  weight,
  share,
  support,
  overIndexShelf: shelf,
  overIndexCatalogue: cat,
})

export const tasteProfile: TasteProfile = {
  creators: [
    facet('Aoi Mizuhara', 21, 0.18, 2, 1.8, 2.4),
    facet('Nanami Sato', 12, 0.1, 1, 1.2, 1.6),
    facet('Ren Kurosawa', 9, 0.08, 1, 1.1, null),
  ],
  genres: [
    facet('Fantasy', 42, 0.24, 5, 1.4, 1.7),
    facet('Drama', 31, 0.18, 4, 1.2, 1.3),
    facet('Slice of Life', 26, 0.15, 3, 1.1, 1.0),
    facet('Mystery', 18, 0.1, 2, 1.5, 2.2),
    facet('Action', 14, 0.08, 3, 0.8, 0.9),
    facet('Romance', 12, 0.07, 2, 1.0, 1.1),
  ],
  tags: [
    facet('Found Family', 20, 0.12, 3, 2.1, 3.4),
    facet('Travel', 14, 0.09, 2, 1.6, 1.9),
    facet('Workplace', 12, 0.08, 2, 1.0, 0.9),
    facet('Detective', 9, 0.06, 1, 1.3, 1.8),
    facet('Cooking', 8, 0.05, 1, 1.0, 1.2),
  ],
  types: [facet('Manga', 80, 0.7, 8, 1.0, 1.1), facet('Manhwa', 24, 0.21, 2, 0.9, 0.8), facet('Manhua', 8, 0.09, 2, 1.0, 1.0)],
  years: [
    { year: 2016, weight: 8, share: 0.07 },
    { year: 2019, weight: 20, share: 0.18 },
    { year: 2021, weight: 34, share: 0.3 },
    { year: 2023, weight: 30, share: 0.26 },
    { year: 2024, weight: 22, share: 0.19 },
  ],
  seriesCount: 10,
  libraryCount: 12,
  catalogueBaselineAvailable: true,
  catalogueBaselineSource: 'readers',
  generatedAt: hoursAgo(3),
}

export const tasteProfileEmpty: TasteProfile = {
  ...tasteProfile,
  creators: [],
  genres: [],
  tags: [],
  types: [],
  years: [],
  seriesCount: 0,
  libraryCount: 0,
  catalogueBaselineAvailable: false,
  catalogueBaselineSource: null,
}

const group = (label: string, tags: string[], ids: number[], size: number, picks: RecommendationItem[]): TasteGroup => ({
  label,
  tags,
  size,
  share: size / 12,
  coherence: 0.62,
  examples: ids.map(member),
  seedIds: ids.map((id) => catalogue[id - 1].mangaBakaId),
  picks,
})

export const tasteInsights: TasteInsights = {
  groups: [
    group('Found Family + Travel', ['Found Family', 'Travel'], [1, 10, 5], 4, [catalogueItem(0), catalogueItem(19), catalogueItem(16), catalogueItem(12), catalogueItem(7)]),
    group('Workplace + Comedy', ['Workplace', 'Comedy'], [2, 8], 3, [catalogueItem(8), catalogueItem(23), catalogueItem(14), catalogueItem(1)]),
    group('Regression + Royalty', ['Regression', 'Royalty'], [6, 3], 3, [catalogueItem(2), catalogueItem(11), catalogueItem(21), catalogueItem(25)]),
  ],
  groupsUnavailable: null,
  oddOneOut: member(11),
  oddOneOutSimilarity: 0.31,
  drift: [
    { bucket: '2025-Q1', seriesCount: 3, similarityToStart: 1, similarityToPrevious: 1, distinctiveTags: ['Found Family', 'Travel'], example: member(1) },
    { bucket: '2025-Q3', seriesCount: 4, similarityToStart: 0.82, similarityToPrevious: 0.82, distinctiveTags: ['Workplace', 'Cooking'], example: member(2) },
    { bucket: '2026-Q1', seriesCount: 5, similarityToStart: 0.64, similarityToPrevious: 0.77, distinctiveTags: ['Regression', 'Royalty'], example: member(6) },
    { bucket: '2026-Q3', seriesCount: 3, similarityToStart: 0.51, similarityToPrevious: 0.8, distinctiveTags: ['Seaside', 'Coming of Age'], example: member(5) },
  ],
  driftUnavailable: null,
  covered: 10,
  total: 12,
  unavailable: null,
  generatedAt: hoursAgo(3),
}

export const tasteInsightsUnavailable: TasteInsights = {
  ...tasteInsights,
  groups: [],
  groupsUnavailable: 'The embedding index is not built yet, so groups cannot be computed.',
  oddOneOut: null,
  oddOneOutSimilarity: null,
  drift: [],
  driftUnavailable: 'Drift needs the embedding index.',
  unavailable: 'The embedding index is not built yet, so there is nothing to compare.',
}

const behaviourSeries = (id: number, value: string, measure: number): BehaviourSeries => ({
  seriesId: id,
  title: catalogue[id - 1].title,
  coverUrl: cover(id),
  value,
  measure,
})

export const readingBehaviour: ReadingBehaviour = {
  seriesStarted: 11,
  seriesFinished: 5,
  finishRate: 0.45,
  medianStopPoint: 0.38,
  medianSecondsPerChapter: 410,
  timedChapters: 640,
  chaptersRead: 1124,
  readingDays: 212,
  medianChaptersPerReadingDay: 5,
  biggestDayCount: 63,
  biggestDay: '2026-03-14',
  savoured: [behaviourSeries(10, '14 min per chapter', 840), behaviourSeries(5, '11 min per chapter', 660)],
  devoured: [behaviourSeries(2, '3 min per chapter', 180), behaviourSeries(8, '4 min per chapter', 240)],
  abandoned: [behaviourSeries(7, 'stopped at 10%', 0.1), behaviourSeries(12, 'stopped at 22%', 0.22)],
  stopPointHistogram: [3, 2, 1, 1, 0, 1, 0, 1, 0, 2],
  generatedAt: hoursAgo(3),
}

export const readingBehaviourEmpty: ReadingBehaviour = {
  ...readingBehaviour,
  seriesStarted: 0,
  seriesFinished: 0,
  finishRate: null,
  medianStopPoint: null,
  medianSecondsPerChapter: null,
  timedChapters: 0,
  chaptersRead: 0,
  readingDays: 0,
  medianChaptersPerReadingDay: null,
  biggestDayCount: null,
  biggestDay: null,
  savoured: [],
  devoured: [],
  abandoned: [],
  stopPointHistogram: [],
}

const stateFor = (id: number, partial: Partial<FeedbackState>): FeedbackState => ({
  mangaBakaId: id,
  suppression: 'none',
  exposure: [],
  dismissedUntilUtc: null,
  revision: 1,
  title: null,
  sentiment: 'none',
  coverUrl: null,
  genres: null,
  updatedAtUtc: hoursAgo(5),
  ...partial,
})

export const feedbackStates: FeedbackState[] = [
  stateFor(BASE_ID + 1, { title: SEEDS[1].title, sentiment: 'liked', coverUrl: cover(SEEDS[1].title), genres: SEEDS[1].genres, updatedAtUtc: hoursAgo(2) }),
  stateFor(BASE_ID + 7, { title: SEEDS[7].title, sentiment: 'disliked', suppression: 'hidden', coverUrl: cover(SEEDS[7].title), genres: SEEDS[7].genres, updatedAtUtc: hoursAgo(20) }),
  stateFor(BASE_ID + 17, { title: SEEDS[17].title, suppression: 'dismissed', dismissedUntilUtc: daysAgo(-24), coverUrl: cover(SEEDS[17].title), genres: SEEDS[17].genres, updatedAtUtc: daysAgo(6) }),
  stateFor(BASE_ID + 4, { title: SEEDS[4].title, exposure: ['anime'], coverUrl: cover(SEEDS[4].title), genres: SEEDS[4].genres, updatedAtUtc: daysAgo(12) }),
]

const activity = (id: number, mangaBakaId: number, title: string, action: string, when: string, effect: string, taste: string): FeedbackActivity => ({
  id,
  mangaBakaId,
  title,
  action,
  occurredAtUtc: when,
  stateRevision: 1,
  dismissedUntilUtc: null,
  queueEffect: effect,
  tasteEffect: taste,
  coverUrl: cover(title),
})

export const feedbackLab: FeedbackLabData = {
  capabilities: { feedback: true, signalOverrides: true, labUi: true, personalAddWeighting: true, animeSignals: true },
  versions: { feedbackRevision: 7, signalRevision: 3 },
  rankingMode: 'semantic',
  summary: {
    visibleShelf: 12,
    personalAdds: 6,
    ratedSources: 4,
    readSources: 10,
    excluded: 1,
    hidden: 1,
    dismissed: 1,
    exposed: 1,
    liked: 1,
    disliked: 1,
    pushingDown: 2,
  },
  avoids: [
    {
      label: 'Mecha',
      kind: 'tag',
      support: 3,
      share: 0.2,
      positiveShare: 0.04,
      positiveSupport: 1,
      shelfCount: 12,
      examples: [{ mangaBakaId: BASE_ID + 7, title: SEEDS[7].title }],
    },
  ],
  sources: catalogue.slice(0, 8).map((c, i) => ({
    mangaBakaId: c.mangaBakaId,
    title: c.title,
    addedAtUtc: i % 2 === 0 ? daysAgo(30 + i * 9) : null,
    rating: i < 4 ? c.rating : null,
    coverUrl: cover(c.id),
    genres: c.genres,
    isRead: c.read > 0,
    excluded: c.id === 3,
  })),
  activity: {
    items: [
      activity(41, BASE_ID + 1, SEEDS[1].title, 'like', hoursAgo(2), 'none', 'positive-title'),
      activity(40, BASE_ID + 7, SEEDS[7].title, 'dislike', hoursAgo(20), 'suppressed', 'negative-taste'),
      activity(38, BASE_ID + 17, SEEDS[17].title, 'dismiss', daysAgo(6), 'suppressed', 'temporary'),
    ],
    nextCursor: null,
    feedbackRevision: 7,
    signalRevision: 3,
  },
}

export const feedbackLabEmpty: FeedbackLabData = {
  ...feedbackLab,
  summary: {
    visibleShelf: 0,
    personalAdds: 0,
    ratedSources: 0,
    readSources: 0,
    excluded: 0,
    hidden: 0,
    dismissed: 0,
    exposed: 0,
    liked: 0,
    disliked: 0,
    pushingDown: 0,
  },
  avoids: [],
  sources: [],
  activity: { items: [], nextCursor: null, feedbackRevision: 0, signalRevision: 0 },
}

export const signalOverrides: SignalOverrideState[] = [{ mangaBakaId: catalogue[2].mangaBakaId, ignoreAsSeed: true, revision: 1 }]

const animeEntry = (key: string, title: string, services: string[], score: number | null, status: AnimeSignalEntry['status'], role: AnimeSignalEntry['role'], manga: number | null, extra: Partial<AnimeSignalEntry> = {}): AnimeSignalEntry => ({
  key,
  services,
  animeCount: 1,
  title,
  score,
  status,
  mangaBakaId: manga ? catalogue[manga - 1].mangaBakaId : null,
  mangaTitle: manga ? catalogue[manga - 1].title : null,
  mangaCoverUrl: manga ? cover(manga) : null,
  role,
  supersededBy: null,
  ...extra,
})

export const animeSignals: AnimeSignalsData = {
  enabled: true,
  instanceEnabled: true,
  lastSyncAtUtc: hoursAgo(9),
  syncing: false,
  progress: null,
  services: ['AniList', 'MyAnimeList'],
  strength: 'balanced',
  strengths: [
    { value: 'subtle', ratingShare: 0.25, topSeedWeight: 0.6 },
    { value: 'balanced', ratingShare: 0.5, topSeedWeight: 0.8 },
    { value: 'full', ratingShare: 1, topSeedWeight: 1 },
  ],
  counts: { total: 7, matched: 5, positive: 2, avoided: 1, neutral: 1, superseded: 1, unmatched: 2 },
  entries: [
    animeEntry('a1', 'Clockwork Tea House: The Animation', ['AniList', 'MyAnimeList'], 8.6, 'Completed', 'positive', 7, { animeCount: 2 }),
    animeEntry('a2', 'Lanterns Over Kasumi', ['AniList'], 9.1, 'Completed', 'positive', 1),
    animeEntry('a3', 'Iron Petal Academy', ['MyAnimeList'], 4.2, 'Dropped', 'avoided', 3),
    animeEntry('a4', 'Paper Moon Detective', ['AniList'], null, 'Watching', 'neutral', 4),
    animeEntry('a5', 'The Ninth Kitchen', ['AniList'], 7.8, 'Completed', 'superseded', 2, { supersededBy: 'library' }),
    animeEntry('a6', 'Sandcastle Dispatch', ['MyAnimeList'], 6.5, 'OnHold', 'unmatched', null),
    animeEntry('a7', 'Moonrise Courier Service', ['AniList'], 8.0, 'Planning', 'unmatched', null),
  ],
}

export const animeSignalsOff: AnimeSignalsData = {
  ...animeSignals,
  enabled: false,
  entries: animeSignals.entries.map((e) => ({ ...e, role: e.mangaBakaId ? 'neutral' : 'unmatched' })),
  counts: { total: 7, matched: 5, positive: 0, avoided: 0, neutral: 5, superseded: 0, unmatched: 2 },
}

export const animeSignalsSyncing: AnimeSignalsData = {
  ...animeSignals,
  syncing: true,
  progress: { looked: 18, total: 42 },
}

export const indexStatusReady: RecommendationIndexStatus = {
  modelPresent: true,
  dumpPresent: true,
  vectorCount: 148_220,
  recommendableTotal: 148_220,
  running: false,
  phase: 'idle',
  embedded: 0,
  scanned: 148_220,
  startedAt: daysAgo(7),
  finishedAt: daysAgo(7),
  lastEmbedded: 0,
  lastError: null,
  estimatedSecondsRemaining: null,
  prebuiltEnabled: true,
  prebuiltInstalledAt: daysAgo(7),
  embeddingModel: 'base',
  useFullDump: false,
  modelSwitching: false,
  modelSwitchError: null,
}

export const indexStatusBuilding: RecommendationIndexStatus = {
  ...indexStatusReady,
  vectorCount: 52_000,
  running: true,
  phase: 'embedding',
  embedded: 12_400,
  scanned: 64_400,
  estimatedSecondsRemaining: 2_700,
  prebuiltInstalledAt: null,
  finishedAt: null,
}

export const indexStatusOff: RecommendationIndexStatus = {
  ...indexStatusReady,
  vectorCount: 0,
  embeddingModel: 'off',
  prebuiltInstalledAt: null,
}

export const indexStatusMissing: RecommendationIndexStatus = {
  ...indexStatusReady,
  vectorCount: 0,
  prebuiltInstalledAt: null,
  startedAt: null,
  finishedAt: null,
  lastError: 'The prebuilt index was built for a different model version.',
}

export const previewReady = (providerId: string): SeriesPreview => ({
  providerId: Number(providerId),
  status: 'ready',
  error: null,
  sourceName: 'MangaDex',
  sourceDisplayName: 'MangaDex',
  chapterLabel: '1',
  pageCount: 6,
  version: 'v1',
  ready: [true, true, true, true, true, true],
})

export const previewFetching = (providerId: string): SeriesPreview => ({
  providerId: Number(providerId),
  status: 'fetching',
  error: null,
  sourceName: 'MangaDex',
  sourceDisplayName: 'MangaDex',
  chapterLabel: '1',
  pageCount: 8,
  version: 'v1',
  ready: [true, true, true, false, false, false, false, false],
})

export const previewFailed = (providerId: string): SeriesPreview => ({
  providerId: Number(providerId),
  status: 'failed',
  error: 'No source could serve the first chapter.',
  sourceName: null,
  sourceDisplayName: null,
  chapterLabel: null,
  pageCount: null,
  version: null,
  ready: [],
})

export const creatorWorks = (): RecommendationItem[] => [
  ownedItem(1, { authorMatch: true }),
  ownedItem(10, { authorMatch: true }),
  ...items(0, 10).map((i) => ({ ...i, authorMatch: true })),
]

export { SEEDS as seedTitles }
