import { http, HttpResponse } from 'msw'
import type { Me } from '../../src/api/auth'

/** Matches `apiRoot` in the real `/initialize.json`, so mocked paths read like the server's routes. */
export const API_ROOT = '/api/v1'

/** `/series/:id` → `/api/v1/series/:id`, for use as an MSW path. */
export const api = (path: string) => `${API_ROOT}${path}`

/**
 * The clock every story runs at (preview.tsx pins `Date` to it), so "3 days ago" and streaks never
 * drift between runs. Build fixture dates from these helpers rather than typing ISO strings.
 */
export const NOW = new Date('2026-10-19T12:00:00Z')
export const minutesAgo = (n: number) => new Date(NOW.getTime() - n * 60_000).toISOString()
export const hoursAgo = (n: number) => minutesAgo(n * 60)
export const daysAgo = (n: number) => hoursAgo(n * 24)
export const daysAhead = (n: number) => daysAgo(-n)

/** A generated cover served by the `/mock/cover/:seed` handler: a gradient with the title on it. */
export const cover = (seed: string | number) => `/mock/cover/${encodeURIComponent(String(seed))}.svg`
/** A generated manga page for the reader, served by `/mock/page/:seed/:n`. */
export const pageImage = (seed: string | number, n: number) => `/mock/page/${encodeURIComponent(String(seed))}/${n}.svg`
/** A generated two-page spread, twice a page's width, its halves marked in right-to-left reading order. */
export const spreadImage = (seed: string | number, n: number) => `/mock/spread/${encodeURIComponent(String(seed))}/${n}.svg`
/** A generated webtoon slice, three times taller than it is wide. */
export const stripImage = (seed: string | number, n: number) => `/mock/strip/${encodeURIComponent(String(seed))}/${n}.svg`
/** A generated wide banner/backdrop image. */
export const banner = (seed: string | number) => `/mock/banner/${encodeURIComponent(String(seed))}.svg`

const ALL_PERMISSIONS: Me['permissionNames'] = [
  'Admin',
  'AddSeries',
  'DeleteSeries',
  'DownloadChapters',
  'ManageDownloadQueue',
  'ManageSources',
  'EditMetadata',
  'ManageTags',
  'ChangeContentRating',
  'UseTrackers',
  'UseOpds',
  'ImportLibrary',
]

export const adminMe: Me = {
  id: 1,
  userName: 'maki',
  displayName: 'Maki',
  permissions: ALL_PERMISSIONS.join(', '),
  permissionNames: ALL_PERMISSIONS,
  isAdmin: true,
  maxContentRating: 'Pornographic',
  allRootFolders: true,
  rootFolderIds: [],
  twoFactorEnabled: false,
  oidcLinked: false,
  oidcUserName: null,
  singleUser: false,
}

/** A signed-in non-admin who reads and requests but cannot add, delete or download directly. */
export const readerMe: Me = {
  ...adminMe,
  id: 2,
  userName: 'reader',
  displayName: 'Rin',
  permissions: 'UseTrackers, UseOpds',
  permissionNames: ['UseTrackers', 'UseOpds'],
  isAdmin: false,
  maxContentRating: 'Suggestive',
  allRootFolders: false,
  rootFolderIds: [1],
}

/** Story-level override for who is signed in. Prefer `parameters: { auth: 'reader' }`. */
export const signedInAs = (me: Me | null) =>
  http.get(api('/auth/me'), () =>
    me ? HttpResponse.json(me) : HttpResponse.json({ error: 'Not signed in' }, { status: 401 }),
  )

/** Answers an endpoint with the app's error body shape. */
export const failWith = (status: number, error = 'Something went wrong on the server', code = 'error.generic') =>
  HttpResponse.json({ code, error }, { status })

/** Never resolves, for loading/skeleton stories. */
export const neverResolve = () => new Promise<never>(() => {})
