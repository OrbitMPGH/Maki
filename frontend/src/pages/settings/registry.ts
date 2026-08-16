import type { Permission } from '../../api/auth'

/**
 * The settings page is a set of tabs, and this is the single description of what lives where.
 *
 * It exists as data rather than as JSX because two very different things read it: the page, which
 * renders a tab's entries in order, and the command palette, which searches every entry the caller
 * is allowed to see and deep-links to it. Keeping one list is what stops a card being reachable by
 * search but missing from its tab, or renamed in one place and not the other.
 *
 * `keywords` is deliberately the words a user would actually type: the labels of the controls
 * *inside* the card, not a restatement of its title. Nobody searches for "Downloads" when what they
 * want is the retry cap.
 */

export type SettingsTabKey =
  | 'account'
  | 'reading'
  | 'library'
  | 'downloads'
  | 'integrations'
  | 'users'
  | 'system'

export interface SettingsTab {
  key: SettingsTabKey
  label: string
  /** Shown under the tab strip, so a tab explains itself before any card is read. */
  description: string
}

export interface SettingsEntry {
  /** Anchor id; also the `s` query parameter the palette deep-links with. */
  id: string
  tab: SettingsTabKey
  title: string
  keywords: string[]
  /** Instance configuration: the server rejects these for a non-admin, so they are not rendered. */
  admin?: boolean
  permission?: Permission
}

export const SETTINGS_TABS: SettingsTab[] = [
  {
    key: 'account',
    label: 'My account',
    description: 'Your login, your API keys, and how Maki looks and opens for you.',
  },
  {
    key: 'reading',
    label: 'Reading',
    description: 'The built-in reader, the OPDS catalogue and what search is allowed to show you.',
  },
  {
    key: 'library',
    label: 'Library',
    description: 'Where files live, how they are named, and where metadata comes from.',
  },
  {
    key: 'downloads',
    label: 'Downloads',
    description: 'Scraper sources, download behaviour and the torrent path.',
  },
  {
    key: 'integrations',
    label: 'Integrations',
    description: 'Kavita, the trackers Maki scrobbles to, and outbound notifications.',
  },
  {
    key: 'users',
    label: 'Users & security',
    description: 'Accounts, permissions, sign-in policy and single sign-on.',
  },
  {
    key: 'system',
    label: 'System',
    description: 'Backups, updates and instance-level details.',
  },
]

export const SETTINGS_ENTRIES: SettingsEntry[] = [
  {
    id: 'account',
    tab: 'account',
    title: 'My account',
    keywords: [
      'password',
      'change password',
      'display name',
      'username',
      'email',
      'api key',
      'token',
      'sessions',
      'sign out',
      'log out',
      'two-factor authentication',
      '2fa',
      'totp',
      'authenticator',
      'link single sign-on',
    ],
  },
  {
    id: 'notification-prefs',
    tab: 'account',
    title: 'Notifications',
    keywords: [
      'bell',
      'inbox',
      'alerts',
      'toast',
      'in-app notifications',
      'new chapters',
      'achievements',
      'level up',
    ],
  },
  {
    id: 'appearance',
    tab: 'account',
    title: 'Appearance',
    keywords: ['theme', 'dark mode', 'light mode', 'accent colour', 'accent color', 'colour'],
  },
  {
    id: 'start-page',
    tab: 'account',
    title: 'Start page',
    keywords: ['landing page', 'home', 'library', 'discover', 'opens on', 'default page'],
  },
  {
    id: 'home-screen',
    tab: 'account',
    title: 'Home screen',
    keywords: [
      'home sections',
      'rails',
      'continue reading',
      'recently added',
      'section order',
      'disable home',
    ],
  },

  {
    id: 'reader',
    tab: 'reading',
    title: 'Reader',
    keywords: [
      'reading direction',
      'right to left',
      'rtl',
      'ltr',
      'webtoon',
      'vertical',
      'double page',
      'page fit',
      'tap zones',
      'auto next chapter',
      'mark read in kavita',
      'import read status',
    ],
  },
  {
    id: 'reading-profiles',
    tab: 'reading',
    title: 'Reading profiles',
    keywords: [
      'profile',
      'manga',
      'manhwa',
      'manhua',
      'webtoon',
      'oel',
      'series type',
      'auto select',
      'per series',
    ],
  },
  {
    id: 'progress',
    tab: 'reading',
    title: 'Progress & achievements',
    keywords: [
      'achievement',
      'badge',
      'level',
      'xp',
      'streak',
      'goal',
      'leaderboard',
      'gamification',
      'time zone',
      'timezone',
    ],
  },
  {
    id: 'opds',
    tab: 'reading',
    title: 'OPDS',
    permission: 'UseOpds',
    keywords: [
      'feed url',
      'catalogue',
      'catalog',
      'panels',
      'chunky',
      'koreader',
      'tachiyomi',
      'mihon',
      'streaming',
      'token',
      'track progress',
    ],
  },
  {
    id: 'discover-rating',
    tab: 'reading',
    title: 'Discover',
    permission: 'ChangeContentRating',
    keywords: ['content rating', 'nsfw', 'erotica', 'mature', 'safe', 'adult'],
  },

  {
    id: 'root-folders',
    tab: 'library',
    title: 'Root Folders',
    admin: true,
    keywords: ['library path', 'storage', 'disk', 'free space', 'folder'],
  },
  {
    id: 'library-files',
    tab: 'library',
    title: 'Library files',
    admin: true,
    keywords: ['comicinfo', 'comicinfo.xml', 'folder naming', 'rename folder', 'imported files'],
  },
  {
    id: 'monitoring',
    tab: 'library',
    title: 'Monitoring',
    admin: true,
    keywords: ['specials', 'omake', 'decimal chapters', 'monitor new items'],
  },
  {
    id: 'metadata',
    tab: 'library',
    title: 'Metadata',
    admin: true,
    keywords: ['mangabaka', 'local database', 'dump', 'snapshot', 'refresh metadata'],
  },
  {
    id: 'recommendations',
    tab: 'library',
    title: 'Recommendations',
    admin: true,
    keywords: ['embeddings', 'embedding model', 'semantic search', 'vectors', 'discover search'],
  },

  {
    id: 'downloads',
    tab: 'downloads',
    title: 'Downloads',
    admin: true,
    keywords: [
      'concurrent',
      'workers',
      'retry',
      'max attempts',
      'backoff',
      'smart download',
      'unread trigger',
    ],
  },
  {
    id: 'sources',
    tab: 'downloads',
    title: 'Sources',
    admin: true,
    keywords: [
      'scrapers',
      'mangadex',
      'mangafire',
      'webtoons',
      'asura',
      'tcb',
      'flame comics',
      'order sources',
      'disable source',
      'auto-match',
      'reorder',
    ],
  },
  {
    id: 'flaresolverr',
    tab: 'downloads',
    title: 'FlareSolverr',
    admin: true,
    keywords: ['cloudflare', 'challenge', 'proxy', '8191'],
  },
  {
    id: 'prowlarr',
    tab: 'downloads',
    title: 'Prowlarr',
    admin: true,
    keywords: [
      'indexer',
      'torrent search',
      'api key',
      'releases',
      'indexers',
      'torznab',
      'categories',
    ],
  },
  {
    id: 'qbittorrent',
    tab: 'downloads',
    title: 'qBittorrent',
    admin: true,
    keywords: ['torrent client', 'category', 'path mapping', 'download client'],
  },

  {
    id: 'kavita-user',
    tab: 'integrations',
    title: 'Kavita reading',
    admin: true,
    keywords: ['attribute reading', 'kavita user', 'progress owner'],
  },
  {
    id: 'kavita',
    tab: 'integrations',
    title: 'Kavita',
    admin: true,
    keywords: ['scan', 'api key', 'path mapping', 'covers', 'library server'],
  },
  {
    id: 'scrobbling',
    tab: 'integrations',
    title: 'Scrobbling',
    permission: 'UseTrackers',
    keywords: [
      'anilist',
      'myanimelist',
      'mal',
      'mangabaka',
      'kitsu',
      'trackers',
      'oauth',
      'client id',
      'client secret',
      'sync interval',
      'plan to read',
    ],
  },
  {
    id: 'notifications',
    tab: 'integrations',
    title: 'Notifications',
    admin: true,
    keywords: ['discord', 'webhook', 'apprise', 'alerts', 'events'],
  },

  {
    id: 'users',
    tab: 'users',
    title: 'Users',
    admin: true,
    keywords: ['accounts', 'permissions', 'invite', 'disable user', 'add user', 'admin'],
  },
  {
    id: 'security',
    tab: 'users',
    title: 'Security',
    admin: true,
    keywords: [
      'https',
      'require https',
      'trusted proxies',
      'lockout',
      'failed attempts',
      'hsts',
      'auth log',
    ],
  },
  {
    id: 'oidc',
    tab: 'users',
    title: 'Single sign-on',
    admin: true,
    keywords: [
      'oidc',
      'openid connect',
      'sso',
      'authelia',
      'authentik',
      'keycloak',
      'issuer',
      'client id',
      'auto provision',
    ],
  },

  {
    id: 'backup',
    tab: 'system',
    title: 'Backup & Restore',
    admin: true,
    keywords: ['backup', 'restore', 'zip', 'retention', 'database', 'export'],
  },
  {
    id: 'updates',
    tab: 'system',
    title: 'Updates',
    admin: true,
    keywords: ['new version', 'check for updates', 'release', 'github'],
  },
  {
    id: 'general',
    tab: 'system',
    title: 'General',
    admin: true,
    keywords: ['port', 'setup guide', 'first-time setup', 'instance'],
  },
]

/** True when the signed-in caller may see this card at all. */
export function entryVisible(
  entry: SettingsEntry,
  isAdmin: boolean,
  can: (permission: Permission) => boolean,
): boolean {
  if (entry.admin && !isAdmin) return false
  if (entry.permission && !can(entry.permission)) return false
  return true
}

/**
 * Substring match over the title, the tab's own label and the keyword list, so "rtl", "cloudflare"
 * and "reading" all land somewhere sensible. Deliberately not fuzzy: a settings list this small
 * gets noisier from fuzziness, not more useful.
 */
export function matchesSettingsQuery(entry: SettingsEntry, query: string): boolean {
  const q = query.trim().toLowerCase()
  if (!q) return false
  if (entry.title.toLowerCase().includes(q)) return true
  if (entry.keywords.some((k) => k.includes(q))) return true
  const tab = SETTINGS_TABS.find((t) => t.key === entry.tab)
  return tab ? tab.label.toLowerCase().includes(q) : false
}

/** Deep link the command palette hands to the router. */
export function settingsPath(entry: SettingsEntry): string {
  return `/settings?tab=${entry.tab}&s=${entry.id}`
}
