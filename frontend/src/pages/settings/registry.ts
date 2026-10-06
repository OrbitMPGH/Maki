import { msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
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
 *
 * Everything a reader sees here is a `MessageDescriptor`, not a string. This module evaluates once,
 * so a rendered string would be stuck in whichever language was active at that moment. Render at
 * the point of use, and note that any `useMemo` doing so **must** list `i18n.locale` in its
 * dependencies: without it the palette keeps matching the previous language's keywords and still
 * appears to work, which is the worst way for this to break.
 *
 * `keywords` is one message per entry holding a comma-separated list, split on render. Fifteen
 * separate messages would hand a translator fifteen disconnected words with no context; one gives
 * them the whole search vocabulary for a card at once, and lets a language add a synonym English
 * has no word for, or drop one that does not apply, without the count having to match.
 *
 * `id` is not translated. It is the anchor and the `s` deep-link parameter.
 */

export type SettingsTabKey =
  | 'account'
  | 'preferences'
  | 'reading'
  | 'library'
  | 'downloads'
  | 'integrations'
  | 'users'
  | 'system'

export interface SettingsTab {
  key: SettingsTabKey
  label: MessageDescriptor
  /** Shown under the tab strip, so a tab explains itself before any card is read. */
  description: MessageDescriptor
}

export interface SettingsEntry {
  /** Anchor id; also the `s` query parameter the palette deep-links with. Never translated. */
  id: string
  tab: SettingsTabKey
  title: MessageDescriptor
  /** A comma-separated list in one message. Split with `entryKeywords`. */
  keywords: MessageDescriptor
  /** Instance configuration: the server rejects these for a non-admin, so they are not rendered. */
  admin?: boolean
  permission?: Permission
}

export const SETTINGS_TABS: SettingsTab[] = [
  {
    key: 'account',
    label: msg`Account`,
    description: msg`Your password, two-factor code, single sign-on link and API keys.`,
  },
  {
    key: 'preferences',
    label: msg`Preferences`,
    description: msg`How Maki looks and behaves for you: colours, language, start page, content rating and what reaches your bell.`,
  },
  {
    key: 'reading',
    label: msg`Reading`,
    description: msg`The built-in reader, progress and achievements, the OPDS catalogue and read sync with Kavita.`,
  },
  {
    key: 'library',
    label: msg`Library`,
    description: msg`Where files live, how they are named, what new series start with, and where metadata comes from.`,
  },
  {
    key: 'downloads',
    label: msg`Downloads`,
    description: msg`The download queue, Smart Download, sources and languages, the torrent clients, and the quality rules that decide when a file is replaced.`,
  },
  {
    key: 'integrations',
    label: msg`Integrations`,
    description: msg`Kavita, tracker accounts and import lists, and alerts to chat and push apps.`,
  },
  {
    key: 'users',
    label: msg`Users & security`,
    description: msg`Accounts, permissions, lockout and session rules, and single sign-on.`,
  },
  {
    key: 'system',
    label: msg`System`,
    description: msg`HTTPS and proxies, backups, the image cache and updates.`,
  },
]

export const SETTINGS_ENTRIES: SettingsEntry[] = [
  {
    id: 'sign-in',
    tab: 'account',
    title: msg`Sign-in`,
    keywords: msg({
      message: `password, change password, sessions, sign out, log out, two-factor authentication, 2fa, totp, authenticator, link single sign-on`,
      comment: `Search terms for the settings command palette, not prose. Translate each term as the word someone would actually type in this language, keep them comma-separated, and add or drop terms freely: the list does not have to match English item for item.`,
    }),
  },
  {
    id: 'api-keys',
    tab: 'account',
    title: msg`API keys`,
    keywords: msg({
      message: `api key, token`,
      comment: `Search terms for the settings command palette, not prose. Translate each term as the word someone would actually type in this language, keep them comma-separated, and add or drop terms freely: the list does not have to match English item for item.`,
    }),
  },

  {
    id: 'appearance',
    tab: 'preferences',
    title: msg`Appearance`,
    keywords: msg({
      message: `theme, dark mode, light mode, accent colour, accent color, colour, match system, auto, follow system`,
      comment: `Search terms for the settings command palette, not prose. Translate each term as the word someone would actually type in this language, keep them comma-separated, and add or drop terms freely: the list does not have to match English item for item.`,
    }),
  },
  {
    id: 'language',
    tab: 'preferences',
    title: msg`Language`,
    // Deliberately overlaps 'title-language' below on the bare word "language": somebody typing it
    // could mean either, and showing both cards is the answer to that rather than guessing.
    keywords: msg({
      message: `language, translation, translate, locale, interface language, ui language, english`,
      comment: `Search terms for the settings command palette, not prose. Translate each term as the word someone would actually type in this language, keep them comma-separated, and add or drop terms freely: the list does not have to match English item for item.`,
    }),
  },
  {
    id: 'title-language',
    tab: 'preferences',
    title: msg`Title language`,
    keywords: msg({
      message: `language, title language, japanese titles, romaji, native title, original title, localised, localized`,
      comment: `Search terms for the settings command palette, not prose. Translate each term as the word someone would actually type in this language, keep them comma-separated, and add or drop terms freely: the list does not have to match English item for item.`,
    }),
  },
  {
    id: 'discover-rating',
    tab: 'preferences',
    title: msg`Content rating`,
    permission: 'ChangeContentRating',
    keywords: msg({
      message: `content rating, nsfw, erotica, mature, safe, adult`,
      comment: `Search terms for the settings command palette, not prose. Translate each term as the word someone would actually type in this language, keep them comma-separated, and add or drop terms freely: the list does not have to match English item for item.`,
    }),
  },
  {
    id: 'home-screen',
    tab: 'preferences',
    title: msg`Start page & Home`,
    keywords: msg({
      message: `home sections, rails, continue reading, recently added, section order, edit layout, drag, reorder, hero, glance, discover layout, disable home, start page, landing page, opens on, default page`,
      comment: `Search terms for the settings command palette, not prose. Translate each term as the word someone would actually type in this language, keep them comma-separated, and add or drop terms freely: the list does not have to match English item for item.`,
    }),
  },
  {
    id: 'series-page',
    tab: 'preferences',
    title: msg`Series page`,
    keywords: msg({
      message: `related series, more like this, similar, recommendations, rails, sequels, spin-offs`,
      comment: `Search terms for the settings command palette, not prose. Translate each term as the word someone would actually type in this language, keep them comma-separated, and add or drop terms freely: the list does not have to match English item for item.`,
    }),
  },
  {
    id: 'notification-prefs',
    tab: 'preferences',
    title: msg`Notifications`,
    keywords: msg({
      message: `bell, inbox, alerts, toast, in-app notifications, new chapters, achievements, level up`,
      comment: `Search terms for the settings command palette, not prose. Translate each term as the word someone would actually type in this language, keep them comma-separated, and add or drop terms freely: the list does not have to match English item for item.`,
    }),
  },

  {
    id: 'reader',
    tab: 'reading',
    title: msg`Reader profiles`,
    keywords: msg({
      message: `reading direction, right to left, rtl, ltr, webtoon, vertical, double page, page fit, tap zones, auto next chapter, reader defaults, reading profiles, profile, manga, manhwa, manhua, oel, series type, auto select, per series`,
      comment: `Search terms for the settings command palette, not prose. Translate each term as the word someone would actually type in this language, keep them comma-separated, and add or drop terms freely: the list does not have to match English item for item.`,
    }),
  },
  {
    id: 'progress',
    tab: 'reading',
    title: msg`Progress & achievements`,
    keywords: msg({
      message: `achievement, badge, level, xp, streak, goal, leaderboard, gamification, time zone, timezone`,
      comment: `Search terms for the settings command palette, not prose. Translate each term as the word someone would actually type in this language, keep them comma-separated, and add or drop terms freely: the list does not have to match English item for item.`,
    }),
  },
  {
    id: 'opds',
    tab: 'reading',
    title: msg`OPDS catalogue`,
    permission: 'UseOpds',
    keywords: msg({
      message: `feed url, catalogue, catalog, panels, chunky, koreader, tachiyomi, mihon, streaming, token, track progress`,
      comment: `Search terms for the settings command palette, not prose. Translate each term as the word someone would actually type in this language, keep them comma-separated, and add or drop terms freely: the list does not have to match English item for item.`,
    }),
  },
  {
    id: 'kavita-sync',
    tab: 'reading',
    title: msg`Kavita read sync`,
    keywords: msg({
      message: `mark read in kavita, push to kavita, import read status, kavita progress, live sync, instant`,
      comment: `Search terms for the settings command palette, not prose. Translate each term as the word someone would actually type in this language, keep them comma-separated, and add or drop terms freely: the list does not have to match English item for item.`,
    }),
  },

  {
    id: 'root-folders',
    tab: 'library',
    title: msg`Root folders`,
    admin: true,
    keywords: msg({
      message: `library path, storage, disk, free space, folder`,
      comment: `Search terms for the settings command palette, not prose. Translate each term as the word someone would actually type in this language, keep them comma-separated, and add or drop terms freely: the list does not have to match English item for item.`,
    }),
  },
  {
    id: 'naming',
    tab: 'library',
    title: msg`Folder & file naming`,
    admin: true,
    keywords: msg({
      message: `folder naming, rename folder, rename files, imported files, chapter format, series folder format, naming tokens, file name, rename all`,
      comment: `Search terms for the settings command palette, not prose. Translate each term as the word someone would actually type in this language, keep them comma-separated, and add or drop terms freely: the list does not have to match English item for item.`,
    }),
  },
  {
    id: 'library-files',
    tab: 'library',
    title: msg`ComicInfo & covers`,
    admin: true,
    keywords: msg({
      message: `comicinfo, comicinfo.xml, cover.jpg, folder poster, library files, komga`,
      comment: `Search terms for the settings command palette, not prose. Translate each term as the word someone would actually type in this language, keep them comma-separated, and add or drop terms freely: the list does not have to match English item for item.`,
    }),
  },
  {
    id: 'monitoring',
    tab: 'library',
    title: msg`New series defaults`,
    admin: true,
    keywords: msg({
      message: `specials, omake, decimal chapters, monitor new items, monitoring, incognito, no scrobble, content rating, add series`,
      comment: `Search terms for the settings command palette, not prose. Translate each term as the word someone would actually type in this language, keep them comma-separated, and add or drop terms freely: the list does not have to match English item for item.`,
    }),
  },
  {
    id: 'metadata',
    tab: 'library',
    title: msg`Metadata database`,
    admin: true,
    keywords: msg({
      message: `mangabaka, local database, dump, snapshot, refresh metadata`,
      comment: `Search terms for the settings command palette, not prose. Translate each term as the word someone would actually type in this language, keep them comma-separated, and add or drop terms freely: the list does not have to match English item for item.`,
    }),
  },
  {
    id: 'recommendations',
    tab: 'library',
    title: msg`Semantic search & recommendations`,
    admin: true,
    keywords: msg({
      message: `embeddings, embedding model, semantic search, vectors, discover search`,
      comment: `Search terms for the settings command palette, not prose. Translate each term as the word someone would actually type in this language, keep them comma-separated, and add or drop terms freely: the list does not have to match English item for item.`,
    }),
  },

  {
    id: 'downloads',
    tab: 'downloads',
    title: msg`Download queue`,
    admin: true,
    keywords: msg({
      message: `concurrent, workers, retry, max attempts, backoff`,
      comment: `Search terms for the settings command palette, not prose. Translate each term as the word someone would actually type in this language, keep them comma-separated, and add or drop terms freely: the list does not have to match English item for item.`,
    }),
  },
  {
    id: 'smart-download',
    tab: 'downloads',
    title: msg`Smart Download`,
    admin: true,
    keywords: msg({
      message: `smart download, unread trigger`,
      comment: `Search terms for the settings command palette, not prose. Translate each term as the word someone would actually type in this language, keep them comma-separated, and add or drop terms freely: the list does not have to match English item for item.`,
    }),
  },
  {
    id: 'read-cleanup',
    tab: 'downloads',
    title: msg`Clean up read chapters`,
    admin: true,
    keywords: msg({
      message: `delete read, auto delete, cleanup, clean up, disk space, retention, remove read chapters`,
      comment: `Search terms for the settings command palette, not prose. Translate each term as the word someone would actually type in this language, keep them comma-separated, and add or drop terms freely: the list does not have to match English item for item.`,
    }),
  },
  {
    id: 'languages',
    tab: 'downloads',
    title: msg`Languages`,
    admin: true,
    keywords: msg({
      message: `language, languages, download language, language priority, preferred language`,
      comment: `Search terms for the settings command palette, not prose. Translate each term as the word someone would actually type in this language, keep them comma-separated, and add or drop terms freely: the list does not have to match English item for item.`,
    }),
  },
  {
    id: 'sources',
    tab: 'downloads',
    title: msg`Sources`,
    admin: true,
    keywords: msg({
      message: `scrapers, manage sources, priority, source priority, source order, best quality, 18+, adult sources, mangadex, mangafire, webtoons, asura, tcb, flame comics, order sources, disable source, auto-match, reorder, dynasty scans, dynasty, yuri, mangalib, toonily, manhwa, gigaviewer, jump+, shonen jump plus, comic days, sunday webry, magcomi, tonari no young jump, zenon, kurage bunch, manhwa18, manhwa18.net, manhwa18net, manhwaweb, manhwa web, manhwaweb.com, shinigami, shngm, olympus, olympus scanlation, olympusxyz, subo de nivel solo, animesama, anime-sama, anime sama, naver, naver webtoon, 네이버 웹툰, manga-tube, mangatube, manga tube, manhuagui, 漫画柜, mhgui, cuutruyen, cứu truyện, cuu truyen, mangaworld, manga world, comic walker, comicwalker, kadocomi, kadokawa, rawkuma, raw, raw manga, ラークマ, mangadenizi, manga denizi, teamx, team-x, team x, olympustaff, taiyo, taiyo.moe`,
      comment: `Search terms for the settings command palette, not prose. Translate each term as the word someone would actually type in this language, keep them comma-separated, and add or drop terms freely: the list does not have to match English item for item.`,
    }),
  },
  {
    id: 'flaresolverr',
    tab: 'downloads',
    title: msg`FlareSolverr`,
    admin: true,
    keywords: msg({
      message: `cloudflare, challenge, proxy, 8191`,
      comment: `Search terms for the settings command palette, not prose. Translate each term as the word someone would actually type in this language, keep them comma-separated, and add or drop terms freely: the list does not have to match English item for item.`,
    }),
  },
  {
    id: 'prowlarr',
    tab: 'downloads',
    title: msg`Prowlarr`,
    admin: true,
    keywords: msg({
      message: `indexer, torrent search, api key, releases, indexers, torznab, categories`,
      comment: `Search terms for the settings command palette, not prose. Translate each term as the word someone would actually type in this language, keep them comma-separated, and add or drop terms freely: the list does not have to match English item for item.`,
    }),
  },
  {
    id: 'qbittorrent',
    tab: 'downloads',
    title: msg`qBittorrent`,
    admin: true,
    keywords: msg({
      message: `torrent client, category, path mapping, download client`,
      comment: `Search terms for the settings command palette, not prose. Translate each term as the word someone would actually type in this language, keep them comma-separated, and add or drop terms freely: the list does not have to match English item for item.`,
    }),
  },
  {
    id: 'profiles',
    tab: 'downloads',
    title: msg`Quality profiles`,
    admin: true,
    keywords: msg({
      message: `upgrade profile, quality profile, cutoff, tier, aggregator, scanlator, official, volume, upgrade until score, page tolerance, minimum score gain, maximum score loss, allow replacing unknown, resolution weight, compression weight, measured quality`,
      comment: `Search terms for the settings command palette, not prose. Translate each term as the word someone would actually type in this language, keep them comma-separated, and add or drop terms freely: the list does not have to match English item for item.`,
    }),
  },
  {
    id: 'formats',
    tab: 'downloads',
    title: msg`Quality formats`,
    admin: true,
    keywords: msg({
      message: `quality format, condition, high resolution, format score, regex, group matches, release name matches, image format, minimum width`,
      comment: `Search terms for the settings command palette, not prose. Translate each term as the word someone would actually type in this language, keep them comma-separated, and add or drop terms freely: the list does not have to match English item for item.`,
    }),
  },
  {
    id: 'upgrades',
    tab: 'downloads',
    title: msg`Upgrade scan`,
    admin: true,
    keywords: msg({
      message: `upgrade, quality profile, default profile, automatic upgrades`,
      comment: `Search terms for the settings command palette, not prose. Translate each term as the word someone would actually type in this language, keep them comma-separated, and add or drop terms freely: the list does not have to match English item for item.`,
    }),
  },
  {
    id: 'volume-releases',
    tab: 'downloads',
    title: msg`Volume releases`,
    admin: true,
    keywords: msg({
      message: `volume, volume packs, volume search, auto-grab, proposals, torrent`,
      comment: `Search terms for the settings command palette, not prose. Translate each term as the word someone would actually type in this language, keep them comma-separated, and add or drop terms freely: the list does not have to match English item for item.`,
    }),
  },

  {
    id: 'kavita',
    tab: 'integrations',
    title: msg`Kavita`,
    admin: true,
    keywords: msg({
      message: `scan, api key, path mapping, covers, library server, attribute reading, kavita user, progress owner, kavita libraries`,
      comment: `Search terms for the settings command palette, not prose. Translate each term as the word someone would actually type in this language, keep them comma-separated, and add or drop terms freely: the list does not have to match English item for item.`,
    }),
  },
  {
    id: 'scrobbling',
    tab: 'integrations',
    title: msg`Trackers`,
    permission: 'UseTrackers',
    keywords: msg({
      message: `anilist, myanimelist, mal, mangabaka, kitsu, trackers, oauth, client id, client secret, sync interval, plan to read`,
      comment: `Search terms for the settings command palette, not prose. Translate each term as the word someone would actually type in this language, keep them comma-separated, and add or drop terms freely: the list does not have to match English item for item.`,
    }),
  },
  {
    id: 'import-lists',
    tab: 'integrations',
    title: msg`Import lists`,
    permission: 'UseTrackers',
    keywords: msg({
      message: `import list, anilist, mal, kitsu, tracker, auto add`,
      comment: `Search terms for the settings command palette, not prose. Translate each term as the word someone would actually type in this language, keep them comma-separated, and add or drop terms freely: the list does not have to match English item for item.`,
    }),
  },
  {
    id: 'notifications',
    tab: 'integrations',
    title: msg`Notification connections`,
    admin: true,
    keywords: msg({
      message: `discord, webhook, telegram, notifiarr, ntfy, gotify, pushover, apprise, slack, mattermost, alerts, events, notifications, outbound, tags, requests, series added, series removed, manual match`,
      comment: `Search terms for the settings command palette, not prose. Translate each term as the word someone would actually type in this language, keep them comma-separated, and add or drop terms freely: the list does not have to match English item for item.`,
    }),
  },

  {
    id: 'users',
    tab: 'users',
    title: msg`Users`,
    admin: true,
    keywords: msg({
      message: `accounts, permissions, disable user, add user, admin`,
      comment: `Search terms for the settings command palette, not prose. Translate each term as the word someone would actually type in this language, keep them comma-separated, and add or drop terms freely: the list does not have to match English item for item.`,
    }),
  },
  {
    id: 'security',
    tab: 'users',
    title: msg`Sign-in policy`,
    admin: true,
    keywords: msg({
      message: `lockout, failed attempts, session lifetime`,
      comment: `Search terms for the settings command palette, not prose. Translate each term as the word someone would actually type in this language, keep them comma-separated, and add or drop terms freely: the list does not have to match English item for item.`,
    }),
  },
  {
    id: 'oidc',
    tab: 'users',
    title: msg`Single sign-on`,
    admin: true,
    keywords: msg({
      message: `oidc, openid connect, sso, authelia, authentik, keycloak, issuer, client id, auto provision`,
      comment: `Search terms for the settings command palette, not prose. Translate each term as the word someone would actually type in this language, keep them comma-separated, and add or drop terms freely: the list does not have to match English item for item.`,
    }),
  },

  {
    id: 'network',
    tab: 'system',
    title: msg`Network`,
    admin: true,
    keywords: msg({
      message: `https, require https, trusted proxies, hsts`,
      comment: `Search terms for the settings command palette, not prose. Translate each term as the word someone would actually type in this language, keep them comma-separated, and add or drop terms freely: the list does not have to match English item for item.`,
    }),
  },
  {
    id: 'backup',
    tab: 'system',
    title: msg`Backup & restore`,
    admin: true,
    keywords: msg({
      message: `backup, restore, zip, retention, database, export`,
      comment: `Search terms for the settings command palette, not prose. Translate each term as the word someone would actually type in this language, keep them comma-separated, and add or drop terms freely: the list does not have to match English item for item.`,
    }),
  },
  {
    id: 'image-cache',
    tab: 'system',
    title: msg`Image cache`,
    admin: true,
    keywords: msg({
      message: `covers, posters, artwork, thumbnails, rebuild image cache, missing covers, broken cover, clear cache, mediacover`,
      comment: `Search terms for the settings command palette, not prose. Translate each term as the word someone would actually type in this language, keep them comma-separated, and add or drop terms freely: the list does not have to match English item for item.`,
    }),
  },
  {
    id: 'updates',
    tab: 'system',
    title: msg`Updates`,
    admin: true,
    keywords: msg({
      message: `new version, check for updates, release, github`,
      comment: `Search terms for the settings command palette, not prose. Translate each term as the word someone would actually type in this language, keep them comma-separated, and add or drop terms freely: the list does not have to match English item for item.`,
    }),
  },
]

/** Old `s` ids that were renamed or split, so bookmarks and stored links still land on a card. */
export const SETTINGS_ENTRY_ALIASES: Record<string, string> = {
  account: 'sign-in',
  'source-languages': 'languages',
  backups: 'backup',
}

/** Old `tab` values. `connections` is still emitted by stored health notifications. */
export const SETTINGS_TAB_ALIASES: Record<string, string> = {
  connections: 'downloads',
}

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
/** The card's search vocabulary, as written for the active language. */
export function entryKeywords(entry: SettingsEntry, render: (m: MessageDescriptor) => string): string[] {
  return render(entry.keywords)
    .split(',')
    .map((k) => k.trim().toLowerCase())
    .filter(Boolean)
}

/**
 * Takes a renderer rather than reading the descriptors itself, because it is a plain function and
 * cannot call a hook. Pass `_` from `useLingui()`, and key the caller's memo on `i18n.locale`.
 */
export function matchesSettingsQuery(
  entry: SettingsEntry,
  query: string,
  render: (m: MessageDescriptor) => string,
): boolean {
  const q = query.trim().toLowerCase()
  if (!q) return false
  if (render(entry.title).toLowerCase().includes(q)) return true
  if (entryKeywords(entry, render).some((k) => k.includes(q))) return true
  const tab = SETTINGS_TABS.find((candidate) => candidate.key === entry.tab)
  return tab ? render(tab.label).toLowerCase().includes(q) : false
}

/** Deep link the command palette hands to the router. */
export function settingsPath(entry: SettingsEntry): string {
  return `/settings?tab=${entry.tab}&s=${entry.id}`
}
