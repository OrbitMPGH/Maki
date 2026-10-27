---
title: Settings, tab by tab
description: What each Settings tab holds, which cards only admins see, and how to link straight to one.
---

This page lists the Settings tabs and the cards inside them. Use it to find where a control lives.

Open Settings from the sidebar, or go to `/settings`. The open tab is kept in the URL, for example `/settings?tab=downloads`. A single card can be linked with the `s` parameter and the card's id, for example `/settings?s=sign-in`. The command palette searches card names and their keywords.

Cards marked **Admin** change the server. They are hidden from accounts that are not admins. Cards that need a permission are hidden from accounts that lack it.

Each row below is one card. The last column lists what the card holds.

## Account

| Card | Who sees it | What it holds |
|---|---|---|
| Sign-in | Everyone | Password, active sessions and sign out, two-factor authentication, linking single sign-on. |
| API keys | Everyone | Creating and revoking the keys used for the REST API. See [API keys and the OpenAPI spec](/Maki/reference/api/). |

## Preferences

| Card | Who sees it | What it holds |
|---|---|---|
| Appearance | Everyone | Theme (light, dark or match the system), accent colour, and motion (reduced motion, faster animations, moving covers, page transitions). |
| Language | Everyone | Interface language. |
| Title language | Everyone | Which form of series titles to show, such as romaji, native or original. |
| Content rating | Everyone with the `ChangeContentRating` permission | Which content ratings you see. |
| Start page & Home | Everyone | The start page, the Home sections, their order and layout. |
| Series page | Everyone | Related-series rails on a series page. |
| Notifications | Everyone | In-app notifications, including new chapters and achievements. |
| Push notifications | Everyone | Browser push notifications, and whether this device receives them. |

## Reading

| Card | Who sees it | What it holds |
|---|---|---|
| Reader profiles | Everyone | Reading direction, page fit, tap zones, auto next chapter, and profiles by series type. |
| Progress & achievements | Everyone | Achievements, levels, streak and reading goal, leaderboard, time zone. |
| OPDS catalogue | Permission `UseOpds` | Your feed URL and token for reading apps, and the Komga address. |
| KOReader progress sync | Permission `UseOpds` | Syncing reading progress with KOReader. |
| Offline reading | Everyone | Saving series to this device for offline reading, and installing Maki as an app. |
| Kavita read sync | Everyone, only when a Kavita connection exists | Pushing read status to Kavita and importing it from Kavita. |

## Library

| Card | Who sees it | What it holds |
|---|---|---|
| Root folders | Everyone | The library folders Maki reads, with the free space on each. |
| Folder & file naming | Admin | Naming templates for series folders and files, and renaming existing ones. |
| File name parsing | Admin | The markers and terms Maki looks for in file names, such as volume and chapter markers. |
| ComicInfo & covers | Admin | ComicInfo.xml and cover image files written next to the series. |
| Page conversion | Admin | Converting pages to WebP or AVIF to save disk space. The wizard also has its own page at `/settings/page-conversion`. |
| Server upscaling | Admin | The upscaling model and CPU threads. |
| Recycle bin | Permission `DeleteSeries` | How long deleted files are kept (14 days by default) and restoring them. |
| New series defaults | Admin | Defaults for new series: monitoring, specials and omake, decimal chapters, incognito. |
| Metadata database | Admin | The local MangaBaka database: its status and a refresh. |
| Semantic search & recommendations | Admin | The embedding model behind semantic search and recommendations. |

## Downloads

Every card on this tab is admin only.

| Card | What it holds |
|---|---|
| Download queue | Concurrent workers, retry count, max attempts and backoff. |
| Smart Download | Smart Download and its unread trigger. |
| Clean up read chapters | Automatic deletion of read chapters, with a retention period. |
| Languages | The languages to download, and their priority. |
| Sources | Source order and priority, which sources are on, and adult sources. |
| FlareSolverr | The FlareSolverr address, used for Cloudflare-protected sources. |
| Indexer | The indexer connection (Prowlarr, Jackett or Torznab) and its categories. |
| Download client | The torrent client (qBittorrent, Transmission or Deluge), its categories and path mapping. |
| Quality profiles | Upgrade profiles: the cutoff score, the minimum gain and maximum loss limits, and the resolution and compression weights. |
| Quality formats | Format scores and the conditions that match them, such as regex, minimum width and image format. |
| Upgrade scan | The default profile and automatic upgrades. |
| Volume releases | Searching for volume packs and grabbing them. |

## Integrations

| Card | Who sees it | What it holds |
|---|---|---|
| Kavita | Admin | The Kavita connection, its libraries, path mapping, and which account owns progress. |
| Suwayomi | Admin | The Suwayomi connection and its extension sources. See [Built-in sources](/Maki/guides/sources/built-in-sources/). |
| Trackers | Permission `UseTrackers` | AniList, MyAnimeList, Kitsu, MangaUpdates and MangaBaka connections, and the sync interval. |
| Import lists | Permission `UseTrackers` | Importing series from tracker lists, with optional automatic adding. |
| Notification connections | Admin | Outgoing notifications to Discord, webhooks, Telegram, ntfy and other services, and the events each one receives. |

## Users & security

Every card on this tab is admin only.

| Card | What it holds |
|---|---|
| Users | Accounts, their permissions, adding users and disabling them. Single-user mode is a control inside this card. |
| Sign-in policy | Lockout after failed attempts, and session lifetime. |
| Sign-in page | The blurred cover mosaic behind the sign-in card. |
| Single sign-on | The OpenID Connect provider: issuer, client details and automatic provisioning. |

## System

Every card on this tab is admin only.

| Card | What it holds |
|---|---|
| Network | The HTTPS requirement, trusted proxies and HSTS. |
| Backup & restore | Creating and restoring backups, and how many to keep. |
| Image cache | Rebuilding the image cache and clearing cached covers. |
| Library health | The scheduled scan time, the low disk threshold and the stale backup warning. |
| Updates | Checking for a new Maki version. |
