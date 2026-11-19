// Everything the doc shots capture. Add or tune shots here; the engine is in lib/, made-up API
// answers in fakes.mjs.
//
// A shot:
//   name       output path under out/, without .png. The folder is the group on the contact sheet.
//   path       app URL, or (data) => URL. data.series and data.chapters hold the ids resolved
//              from SERIES and CHAPTERS below.
//   capture    'viewport' (default), 'full', or a locator:
//                { panel: 'Title' }   the bordered card whose heading starts with Title
//                { rail: 'Title' }    a section header and what follows it up to the next one
//                                     (blocks: n to stop sooner)
//                { heading: 'Title' } a heading and its siblings up to the next heading like it
//                { css: '.x' }        the first visible match (nth or last to pick another)
//                { text: 'Label', closest: '.x' } the element holding that text, or its ancestor
//                { rows: { from: 'Ch. 1', to: 'Ch. 9' } } a table's header and a run of its rows
//              Any locator takes within: '.css' to search inside one element.
//   steps      (or (data) => steps) run in order after load:
//                { click: 'Text' | { css, text, nth } | { label } } add real: true for a pointer click
//                { tap: [x, y] }      fractions of the viewport
//                { type: 'text', into: '.css' }, { upload: '/abs/file', into: 'input[type=file]' }
//                { key }, { hover: '.css' }, { waitFor: '.css' }, { wait: ms }, { scroll: y }
//                { hub: [{ target, arguments }] } SignalR events, { eval: fn }
//              Each step takes settle: ms for the pause after it.
//   rewrites   made-up answers, see fakes.mjs and the README
//   signedOut  true opens the page with no key or cookie
//   viewport   { width, height } for desktop (default 1600 x 1000); device: 'phone' | 'tablet'
//   accent, background, localStorage, sessionStorage, css, pad, settleMs, note

import { fileURLToPath } from 'node:url'
import * as FAKE from './fakes.mjs'

export const DEFAULTS = {
  scale: 2,
  background: 'tinted',
  accent: 'blush',
  localStorage: {
    'star-nudge-dismissed': '1',
    'maki-language': 'en',
    // Top-bar layout: the sidebar collapsed into the header. Shots that want the sidebar set '0'.
    'sidebar-collapsed': '1',
  },
}

const SIDEBAR = { 'sidebar-collapsed': '0' }

// Series the shots use, matched by title. Ids differ between rebuilds of the showcase config.
export const SERIES = {
  frieren: 'Frieren: Beyond Journey',
  kaguya: 'Kaguya-sama',
  onePiece: 'ONE PIECE',
  ouran: 'Ouran High School Host Club',
}

// Chapters the shots use. The reader chapter needs a real file on disk (see the README).
export const CHAPTERS = {
  reader: { series: 'frieren', number: 63 },
  readerLast: { series: 'frieren', number: 64 },
}

// Chapter states the prepare step writes into the showcase database before each run.
export const STATES = {
  // Read, then removed by read-file cleanup.
  removed: [{ series: 'Frieren', numbers: [18, 19, 20] }],
  // Gone from the sources that listed it.
  delisted: [{ series: 'Frieren', numbers: [2.2, 30] }],
  // The health scan found these files gone from disk. Ch. 30 is also delisted, which raises the
  // "chapters gone from the source" alert because it still has a file.
  missing: [
    { series: 'Frieren', numbers: [27, 28] },
    { series: 'ONE PIECE', numbers: [3, 4, 5, 6] },
  ],
  // Wanted with no file, so the series page offers "Download N wanted".
  wanted: [{ series: 'Frieren', numbers: [65, 66, 67, 68, 69, 70, 71, 72] }],
  // Archived series, for the Archived list.
  archived: ['Ouran High School Host Club'],
}

const ago = (minutes) => new Date(Date.now() - minutes * 60000).toISOString()

// Rewrites that apply to every shot.
export const REWRITES = [
  {
    // A local build reports v0.0.0-dev. Show the latest release, and no "new since upgrade" dots.
    match: /^\/api\/v1\/system\/status$/,
    apply: (body, d) => ({ ...body, ...(d.version ? { version: d.version.replace(/^v/, ''), isDevBuild: false } : {}), upgradedFrom: null }),
  },
  { match: /^\/api\/v1\/settings\/setup$/, apply: (body) => ({ ...body, completed: true }) },
  { match: /^\/api\/v1\/settings\/announcements$/, apply: (body) => ({ ...body, language: false, appearance: false }) },
  ...FAKE.SOURCE_SEARCH,
  ...FAKE.TRACKERS,
]

// The queue as it looks mid-download: a series torrent grab plus a couple of scraper chapters.
function busyQueue(d) {
  const kaguya = d.series.kaguya
  const frieren = d.series.frieren
  const base = { retryCount: 0, nextAttempt: null, errorKey: null, errorParams: null, errorMessage: null, completedAt: null, origin: 'manual', upgrade: null, priority: 'normal', rootFolderId: 1, chapterTitle: null }
  return [
    {
      ...base,
      id: 90001,
      chapterId: 0,
      seriesId: kaguya,
      seriesTitle: 'Kaguya-sama: Love is War',
      releaseTitle: 'Kaguya-sama - Love is War v01-v28 (2016-2023) (Digital) (1r0n)',
      chapterVolume: null,
      chapterNumber: null,
      sourceName: 'torrent',
      status: 'Downloading',
      pagesTotal: 100,
      pagesDone: 42,
      queuedAt: ago(9),
      seriesCoverUrl: `/api/v1/mediacover/${kaguya}/cover.jpg`,
    },
    {
      ...base,
      id: 90002,
      chapterId: null,
      seriesId: frieren,
      seriesTitle: "Frieren: Beyond Journey's End",
      releaseTitle: null,
      chapterVolume: null,
      chapterNumber: '65',
      sourceName: 'mangadex',
      status: 'Downloading',
      pagesTotal: 18,
      pagesDone: 11,
      queuedAt: ago(2),
      origin: 'monitor',
      seriesCoverUrl: `/api/v1/mediacover/${frieren}/cover.jpg`,
    },
    {
      ...base,
      id: 90003,
      chapterId: null,
      seriesId: frieren,
      seriesTitle: "Frieren: Beyond Journey's End",
      releaseTitle: null,
      chapterVolume: null,
      chapterNumber: '66',
      sourceName: 'mangadex',
      status: 'Queued',
      pagesTotal: 0,
      pagesDone: 0,
      queuedAt: ago(2),
      origin: 'monitor',
      seriesCoverUrl: `/api/v1/mediacover/${frieren}/cover.jpg`,
    },
  ]
}

const QUEUE_BUSY = [
  {
    match: /^\/api\/v1\/queue\?/,
    apply: (body, d) => {
      const seriesId = Number(d.url.searchParams.get('seriesId')) || null
      const items = busyQueue(d).filter((i) => !seriesId || i.seriesId === seriesId)
      return { ...body, items: [...items, ...body.items], total: body.total + items.length }
    },
  },
  { match: /^\/api\/v1\/queue\/summary$/, apply: (body) => ({ ...body, active: 3 }) },
]

// Source matching caught halfway: two sources mapped, one found, one still searching.
const MATCHING = [
  { match: /^\/api\/v1\/series\/\d+$/, apply: (body) => ({ ...body, sourceMatchPending: true }) },
  { match: /^\/api\/v1\/sourcemapping\?seriesId=/, apply: (body) => body.filter((m) => ['mangadex', 'atsumaru'].includes(m.sourceName)) },
]
const matchingEvents = (seriesId) => [
  { target: 'sourceMatchProgress', arguments: [{ seriesId, sourceName: 'weebcentral', state: 'Matched' }] },
  { target: 'sourceMatchProgress', arguments: [{ seriesId, sourceName: 'topmanhua', state: 'Searching' }] },
  { target: 'sourceMatchProgress', arguments: [{ seriesId, sourceName: 'mangapill', state: 'Searching' }] },
]

const frieren = (d) => `/series/${d.series.frieren}`
const frierenChapters = (d) => `/series/${d.series.frieren}?tab=chapters`
const reader = (d) => `/read/${d.chapters.reader}`
const settings = (id) => ({ path: `/settings?s=${id}`, capture: { css: `#setting-${id}` }, settleMs: 1800 })

// Shows the reader bars, which start hidden.
// They hide again after four seconds unless the pointer rests on one, hence the hover.
const SHOW_READER_BARS = [{ waitFor: '.reader-bar-bottom' }, { wait: 1200 }, { tap: [0.5, 0.5] }, { wait: 600, settle: 0 }, { hover: '.reader-bar-top' }]

export const SHOTS = [
  // Whole pages, desktop
  { name: 'pages/home', path: '/home', capture: 'full' },
  { name: 'pages/home-top', path: '/home' },
  { name: 'pages/home-sidebar', path: '/home', localStorage: SIDEBAR, note: 'Sidebar layout' },
  { name: 'pages/library', path: '/library' },
  { name: 'pages/library-sidebar', path: '/library', localStorage: SIDEBAR, note: 'Sidebar layout' },
  { name: 'pages/library-full', path: '/library', capture: 'full' },
  { name: 'pages/discover', path: '/discover' },
  { name: 'pages/discover-full', path: '/discover', capture: 'full', note: 'The whole scrollable page' },
  { name: 'pages/discover-recommended', path: '/discover/recommended', capture: 'full' },
  { name: 'pages/discover-taste', path: '/discover/taste', capture: 'full' },
  { name: 'pages/discover-shortlist', path: '/discover/shortlist' },
  { name: 'pages/series', path: frieren },
  { name: 'pages/series-full', path: frieren, capture: 'full' },
  { name: 'pages/series-sidebar', path: frieren, localStorage: SIDEBAR, note: 'Sidebar layout' },
  { name: 'pages/series-chapters', path: frierenChapters, capture: 'full' },
  { name: 'pages/series-files', path: (d) => `/series/${d.series.frieren}?tab=files` },
  { name: 'pages/activity', path: '/activity', rewrites: QUEUE_BUSY },
  { name: 'pages/activity-upcoming', path: '/activity?tab=upcoming', capture: 'full' },
  { name: 'pages/activity-upgrades', path: '/activity?tab=upgrades' },
  { name: 'pages/health', path: '/health', capture: 'full' },
  { name: 'pages/health-files', path: '/health?tab=files' },
  { name: 'pages/health-history', path: '/health?tab=history' },
  { name: 'pages/stats', path: '/stats', capture: 'full' },
  { name: 'pages/scrobble', path: '/scrobble', capture: 'full' },
  { name: 'pages/import', path: '/import' },
  { name: 'pages/requests', path: '/requests' },
  { name: 'pages/notifications', path: '/notifications' },
  { name: 'pages/add-series', path: '/add' },
  ...['account', 'preferences', 'reading', 'library', 'downloads', 'integrations', 'users', 'system'].map((tab) => ({
    name: `pages/settings-${tab}`,
    path: `/settings?tab=${tab}`,
    capture: 'full',
  })),

  // Whole pages, phone
  { name: 'phone/home', path: '/home', device: 'phone' },
  { name: 'phone/library', path: '/library', device: 'phone' },
  { name: 'phone/discover', path: '/discover', device: 'phone' },
  { name: 'phone/series', path: frieren, device: 'phone' },
  { name: 'phone/reader', path: reader, device: 'phone', settleMs: 1500 },
  { name: 'phone/stats', path: '/stats', device: 'phone' },

  // Home
  { name: 'home/continue-reading', path: '/home', capture: { css: '.continue-section' } },
  { name: 'home/next-up', path: '/home', capture: { css: '.continue-tray' } },
  { name: 'home/last-5-weeks', path: '/home', capture: { css: 'section:has(h2)' } },
  { name: 'home/continue-from-anime', path: '/home', capture: { rail: 'Continue from the anime' } },
  { name: 'home/recently-added', path: '/home', capture: { rail: 'Recently added' } },

  // Series page
  { name: 'series/hero', path: frieren, capture: { css: '.series-hero' }, pad: 0 },
  { name: 'series/progress', path: frieren, capture: { panel: 'Progress' } },
  { name: 'series/anime-notice', viewport: { width: 1000 }, path: frieren, capture: { css: '.anime-resume-callout' } },
  { name: 'series/anime-coverage', path: frieren, capture: { heading: 'Anime coverage' } },
  { name: 'series/your-reading', path: frieren, capture: { panel: 'Your reading' } },
  { name: 'series/sources', path: frieren, capture: { panel: 'Sources' } },
  {
    name: 'series/sources-matching',
    path: frieren,
    rewrites: MATCHING,
    steps: (d) => [{ wait: 1500 }, { hub: matchingEvents(d.series.frieren) }],
    capture: { panel: 'Sources' },
    note: 'Faked: matching paused halfway',
  },
  { name: 'series/next-chapter', path: frieren, capture: { css: '.release-outlook' } },
  { name: 'series/rating', path: frieren, steps: [{ click: { css: '.rating-pill' } }, { waitFor: '.rating-panel' }], capture: { css: '.mantine-Popover-dropdown:has(.rating-panel)' }, pad: 0 },
  {
    name: 'series/rating-breakdown',
    path: frieren,
    steps: [{ click: { css: '.rating-pill' } }, { waitFor: '.rating-panel' }, { click: 'Break it down' }, { waitFor: '.rating-cats' }],
    capture: { css: '.mantine-Popover-dropdown:has(.rating-panel)' },
    pad: 0,
  },
  { name: 'series/scrobbling', path: frieren, capture: { rail: 'Scrobbling' } },
  { name: 'series/related', path: frieren, capture: { rail: 'Related series' } },
  { name: 'series/more-like-this', path: frieren, capture: { rail: 'More like this' } },
  { name: 'series/torrent-download', path: (d) => `/series/${d.series.kaguya}`, rewrites: QUEUE_BUSY, note: 'Faked: a series torrent at 42%' },

  // Chapters
  { name: 'chapters/table', path: frierenChapters, capture: { css: 'table' } },
  { name: 'chapters/states', path: frierenChapters, capture: { rows: { from: 'Ch. 16', to: 'Ch. 29' } }, pad: 0, note: 'Read, auto-removed, downloaded, file missing' },
  { name: 'chapters/delisted', path: frierenChapters, capture: { rows: { from: 'Ch. 1', to: 'Ch. 3' } }, pad: 0, note: 'Ch. 2.2 is delisted' },
  {
    name: 'chapters/volumes',
    path: (d) => `/series/${d.series.kaguya}?tab=chapters`,
    localStorage: { 'series-chapter-grouping': 'on', 'series-chapter-sort': 'asc' },
    // A viewport, not a crop: the volume headings are sticky and only sit right in a scrolled page.
    steps: [{ eval: () => window.scrollTo(0, document.querySelector('table').getBoundingClientRect().top + scrollY - 140) }, { wait: 600 }],
  },
  { name: 'chapters/long-series', path: (d) => `/series/${d.series.onePiece}?tab=chapters` },

  // Reader
  { name: 'reader/page', path: reader, settleMs: 1500 },
  { name: 'reader/bars', path: reader, steps: SHOW_READER_BARS },
  {
    name: 'reader/page-bar',
    path: reader,
    steps: [...SHOW_READER_BARS, { hover: '.reader-scrub-segment:nth-child(8)' }, { wait: 800 }],
    note: 'Hovering the page bar shows a page preview',
  },
  {
    name: 'reader/settings-panel',
    path: reader,
    steps: [...SHOW_READER_BARS, { click: { label: 'Reader settings' } }, { waitFor: '.reader-settings' }],
    capture: { css: '.reader-settings' },
    pad: 0,
  },
  { name: 'reader/settings-open', path: reader, steps: [...SHOW_READER_BARS, { click: { label: 'Reader settings' } }, { waitFor: '.reader-settings' }] },

  // Discover
  { name: 'discover/hero', path: '/discover', capture: { css: '.discover-hero' }, pad: 0 },
  { name: 'discover/rail', path: '/discover', capture: { rail: 'Based on your recent activity' } },
  {
    name: 'discover/detail-modal',
    path: '/discover',
    steps: [{ click: { css: '.discover-card-action' } }, { waitFor: '.discover-modal-root' }, { wait: 1500 }],
    capture: { css: '.mantine-Modal-content:has(.discover-modal-root)' },
    pad: 0,
  },

  // Activity
  { name: 'activity/queue-torrent', path: '/activity', rewrites: QUEUE_BUSY, capture: { css: '[role=tabpanel]' }, note: 'Faked: a torrent and two chapters in flight' },
  { name: 'activity/history', path: '/activity', capture: { rail: 'History' } },
  { name: 'activity/upcoming', path: '/activity?tab=upcoming', capture: { css: '[role=tabpanel]' } },

  // Health
  { name: 'health/running-now', path: '/health', capture: { rail: 'Running now' } },
  { name: 'health/system-checks', path: '/health', capture: { rail: 'System checks' } },
  { name: 'health/scheduled-jobs', path: '/health', capture: { rail: 'Scheduled jobs' } },
  { name: 'health/image-cache', path: '/health', capture: { rail: 'Image cache and backups' } },

  // Library
  { name: 'library/grid', path: '/library', capture: { css: '.mantine-SimpleGrid-root' } },

  // Settings sections
  ...[
    'appearance',
    'home-screen',
    'series-page',
    'reader',
    'keyboard-shortcuts',
    'ratings',
    'offline-reading',
    'koreader-sync',
    'root-folders',
    'naming',
    'page-conversion',
    'upscaling',
    'monitoring',
    'downloads',
    'languages',
    'sources',
    'indexer',
    'downloadclient',
    'profiles',
    'upgrades',
    'scrobbling',
    'import-lists',
    'notifications',
    'backup',
    'network',
    'sign-in',
    'signin-page',
    'oidc',
    'users',
    'notification-prefs',
    'push-notifications',
    'language',
    'progress',
    'rating-prompts',
    'reading-status',
    'auto-tag-rules',
    'drop-folder',
    'library-files',
    'root-folder-rules',
    'recycle-bin',
    'read-cleanup',
    'credit-scan',
    'metadata',
    'recommendations',
    'smart-download',
    'flaresolverr',
    'formats',
    'volume-releases',
    'kavita',
    'suwayomi',
    'opds',
    'health',
    'updates',
  ].map((id) => ({ name: `settings/${id}`, ...settings(id) })),

  // Menus and dialogs that open on the showcase as it is
  { name: 'home/edit-layout', path: '/home', steps: [{ click: 'Edit layout' }, { wait: 600 }] },
  { name: 'home/new-rail', path: '/home', steps: [{ click: 'Edit layout' }, { click: 'Add a rail' }, { waitFor: '.mantine-Modal-content' }], capture: { css: '.mantine-Modal-content' }, pad: 0 },
  { name: 'discover/edit-layout', path: '/discover', steps: [{ click: { label: 'More options' } }, { waitFor: '.mantine-Menu-dropdown' }, { click: { css: '.mantine-Menu-dropdown *', text: 'Edit layout' } }, { wait: 600 }] },
  { name: 'discover/side-interests', path: '/discover', capture: { rail: 'Another side of your library' } },
  { name: 'discover/readers-like-you', path: '/discover', capture: { rail: 'Readers like you also finished' } },
  { name: 'discover/trending', path: '/discover', capture: { rail: 'Trending now' } },
  { name: 'discover/following', path: '/discover', capture: { rail: 'New from creators you follow' } },
  { name: 'discover/genre-wall', path: '/discover', capture: { css: '.discover-genre-wall' } },
  { name: 'discover/show-more', path: '/discover', steps: [{ click: { text: 'Show more', tag: 'button, a' } }, { wait: 1200 }] },
  {
    name: 'discover/feedback-menu',
    path: '/discover',
    steps: [
      { click: { css: '.discover-card-action' } },
      { waitFor: '.discover-modal-root' },
      { wait: 1200 },
      { click: { label: 'More recommendation feedback actions' } },
      { waitFor: '.mantine-Menu-dropdown' },
    ],
  },
  { name: 'discover/taste-signals', path: '/discover/taste', capture: { rail: 'What shapes your recommendations' } },
  { name: 'discover/manage-signals', path: '/discover/taste', steps: [{ click: 'Manage signals' }, { waitFor: '.mantine-Modal-content' }, { wait: 800 }], capture: { css: '.mantine-Modal-content' }, pad: 0 },
  {
    name: 'discover/hidden-everywhere',
    path: '/discover',
    rewrites: FAKE.HIDDEN_EVERYWHERE,
    steps: [{ click: { text: 'Show more', tag: 'button, a' } }, { wait: 1200 }, { click: 'Hidden everywhere' }, { waitFor: '.mantine-Modal-content' }],
    capture: { css: '.mantine-Modal-content' },
    pad: 0,
    note: 'Faked: two hidden genres and a tag',
  },

  { name: 'activity/dropdown', path: '/home', rewrites: QUEUE_BUSY, steps: [{ click: { css: '[aria-label^="Activity"]' } }, { waitFor: '.mantine-Popover-dropdown' }], capture: { css: '.mantine-Popover-dropdown' }, note: 'Faked queue' },
  { name: 'activity/pause-panel', path: '/activity', steps: [{ click: 'Pause downloads' }, { waitFor: '.mantine-Popover-dropdown' }], capture: { css: '.mantine-Popover-dropdown' } },
  { name: 'activity/history-run', path: '/activity', steps: [{ click: { css: '.history-run-head' } }], capture: { rail: 'History' } },

  { name: 'health/scan-menu', path: '/health', steps: [{ click: 'Scan files' }, { waitFor: '.mantine-Menu-dropdown' }], capture: { css: '.mantine-Menu-dropdown' } },

  { name: 'settings/notification-editor', path: '/settings?s=notifications', viewport: { height: 1500 }, steps: [{ wait: 1500 }, { click: 'Add connection' }, { waitFor: '.mantine-Modal-content' }], capture: { css: '.mantine-Modal-content' }, pad: 0 },
  { name: 'settings/user-editor', path: '/settings?s=users', viewport: { height: 1500 }, steps: [{ wait: 1500 }, { click: 'Add user' }, { waitFor: '.mantine-Modal-content' }], capture: { css: '.mantine-Modal-content' }, pad: 0 },
  { name: 'settings/manage-sources', path: '/settings?s=sources', steps: [{ wait: 1500 }, { click: 'Manage sources' }, { waitFor: '.mantine-Modal-content' }, { wait: 800 }], capture: { css: '.mantine-Modal-content' }, pad: 0 },
  {
    name: 'settings/manage-sources-flaresolverr',
    path: '/settings?s=sources',
    steps: [{ wait: 1500 }, { click: 'Manage sources' }, { waitFor: '.mantine-Modal-content' }, { type: 'Flame', into: '[aria-label="Search sources"]' }, { wait: 800 }],
    capture: { css: '.mantine-Modal-content' },
    pad: 0,
  },
  {
    name: 'settings/custom-source-wizard',
    path: '/settings?s=sources',
    steps: [{ wait: 1500 }, { click: 'Manage sources' }, { waitFor: '.mantine-Modal-content' }, { click: 'Add source' }, { click: 'Set up a site' }, { wait: 800 }],
    capture: { css: '.mantine-Modal-content', last: true },
    pad: 0,
  },
  { name: 'settings/quality-profile', path: '/settings?s=profiles', steps: [{ wait: 1500 }, { click: { css: '#setting-profiles button, #setting-profiles [role=button]', text: 'Best copy' } }, { wait: 800 }], capture: { css: '#setting-profiles' } },
  { name: 'pages/settings-new', path: '/settings?tab=new', rewrites: [{ match: /^\/api\/v1\/system\/status$/, apply: (b) => ({ ...b, upgradedFrom: '0.36.0' }) }], capture: 'full', note: 'Faked: upgraded from 0.36.0' },
  { name: 'pages/page-conversion-setup', path: '/settings/page-conversion', steps: [{ click: { label: 'Convert pages' } }, { wait: 2500 }], capture: 'full', note: 'Conversion switched on, not saved' },

  { name: 'series/link-source', path: frieren, steps: [{ click: 'Link source' }, { waitFor: '.mantine-Modal-content' }, { wait: 600 }], capture: { css: '.mantine-Modal-content' }, pad: 0 },
  { name: 'series/remove-source', path: frieren, steps: [{ click: { label: 'Remove mapping' } }, { waitFor: '.mantine-Modal-content' }], capture: { css: '.mantine-Modal-content' }, pad: 0 },
  { name: 'series/more-actions', path: frieren, steps: [{ click: { css: '.series-hero [aria-label="More actions"]' } }, { waitFor: '.mantine-Menu-dropdown' }], capture: { css: '.mantine-Menu-dropdown' } },
  {
    name: 'series/automation',
    path: frieren,
    steps: [{ click: { css: '.series-hero [aria-label="More actions"]' } }, { waitFor: '.mantine-Menu-dropdown' }, { click: { css: '.mantine-Menu-dropdown *', text: 'Automation' } }, { wait: 600 }],
    capture: { css: '.mantine-Menu-dropdown' },
  },

  { name: 'stats/range', path: '/stats', capture: { css: '.stats-range-group' } },
  { name: 'stats/reading', path: '/stats', capture: { css: '#stats-reading' } },
  { name: 'stats/progress', path: '/stats', capture: { css: '#stats-progress' } },
  { name: 'stats/rewind', path: '/stats', steps: [{ click: 'Play Rewind' }, { waitFor: '.rewind-overlay' }, { wait: 2500 }] },

  { name: 'library/table', path: '/library?view=table&groupBy=status' },
  { name: 'library/filters', path: '/library', steps: [{ click: 'Filters' }, { waitFor: '.library-filter-root' }], capture: { css: '.library-filter-root' } },
  {
    name: 'library/select',
    path: '/library',
    steps: [{ click: { label: 'Select' } }, { click: { css: '.library-card', nth: 0 } }, { click: { css: '.library-card', nth: 2 } }, { click: { css: '.library-card', nth: 3 } }, { wait: 600 }],
  },

  {
    name: 'reader/settings-enhance',
    path: reader,
    steps: [
      ...SHOW_READER_BARS,
      { click: { label: 'Reader settings' } },
      { waitFor: '.reader-settings' },
      { click: { css: '.reader-settings button, .reader-settings [role=button]', text: 'Enhance' } },
      { wait: 800 },
    ],
    capture: { css: '.reader-settings' },
    pad: 0,
  },
  {
    name: 'reader/profile-menu',
    path: reader,
    steps: [...SHOW_READER_BARS, { click: { label: 'Reader settings' } }, { waitFor: '.reader-settings' }, { click: { css: '.reader-settings-chip' } }, { waitFor: '.mantine-Menu-dropdown' }],
  },
  { name: 'reader/shortcuts', path: reader, steps: [{ waitFor: '.reader-bar-bottom' }, { wait: 1000 }, { key: '?' }, { wait: 800 }], capture: { css: '.reader-shortcuts-panel' } },
  {
    name: 'reader/chapter-end',
    path: (d) => `/read/${d.chapters.readerLast}`,
    steps: [{ waitFor: '.reader-bar-bottom' }, { wait: 1200 }, { key: 'End' }, { wait: 1200 }, { key: 'ArrowLeft' }, { wait: 1500 }],
  },

  // States the prepare step stages
  { name: 'series/delisted-alert', path: frieren, capture: { text: '1 downloaded chapter is no longer listed', closest: '.callout' } },
  { name: 'series/archived-banner', viewport: { width: 1000 }, path: (d) => `/series/${d.series.ouran}`, capture: { text: 'Restore to library', closest: '.callout' } },
  { name: 'pages/library-archived', path: '/library/archived' },
  { name: 'chapters/download-wanted', path: frierenChapters, steps: [{ click: { label: 'More download options' } }, { waitFor: '.mantine-Menu-dropdown' }], note: 'Frieren 65 to 72 staged as wanted' },
  { name: 'health/files-findings', path: '/health?tab=files', capture: { css: '[role=tabpanel]' } },

  // Faked answers (fakes.mjs)
  { name: 'series/rating-checkpoint', viewport: { width: 1000 }, path: frieren, rewrites: FAKE.RATING_CHECKPOINT, capture: { css: '.rating-prompt' }, note: 'Faked: checkpoint "hooked" at a provisional 7' },
  { name: 'series/rate-hint', viewport: { width: 1000 }, path: frieren, rewrites: FAKE.RATE_HINT, capture: { css: '.series-rate-hint' }, note: 'Faked: finished and unrated' },
  { name: 'series/rating-history', path: frieren, rewrites: FAKE.RATING_HISTORY, steps: [{ wait: 1000 }, { click: { label: 'Your rating over time', real: true } }, { wait: 800 }, { hover: '.series-hero-title' }], capture: { text: '8/10 so far', closest: '[data-position]' }, note: 'Faked history' },
  { name: 'series/archive-hint', viewport: { width: 1000 }, path: frieren, rewrites: FAKE.ARCHIVE_HINT, capture: { css: '.series-archive-hint' }, note: 'Faked' },
  {
    name: 'series/archive-dialog',
    path: frieren,
    rewrites: FAKE.ARCHIVE_DIALOG,
    steps: [{ click: { css: '.series-hero [aria-label="More actions"]' } }, { waitFor: '.mantine-Menu-dropdown' }, { click: { css: '.mantine-Menu-dropdown [role=menuitem]', text: 'Archive' } }, { waitFor: '.mantine-Modal-content' }],
    capture: { css: '.mantine-Modal-content' },
    pad: 0,
    note: 'Faked: unrated, with a provisional 8',
  },
  { name: 'series/near-miss', path: frieren, rewrites: FAKE.NEAR_MISS, capture: { panel: 'Sources' }, note: 'Faked "Is this it?" suggestion' },
  {
    name: 'series/compare-sources',
    path: frieren,
    rewrites: FAKE.COMPARE,
    steps: [
      { click: { label: 'More source actions' } },
      { waitFor: '.mantine-Menu-dropdown' },
      { click: { css: '.mantine-Menu-dropdown [role=menuitem]', text: 'Compare' } },
      { waitFor: '.mantine-Modal-content' },
      { wait: 2500 },
      { click: { css: '.mantine-Modal-content label', text: 'Hide source names' } },
      { wait: 600 },
    ],
    capture: { css: '.mantine-Modal-content' },
    pad: 0,
    note: 'Faked comparison of three sources',
  },
  {
    name: 'series/undo-conversion',
    path: frieren,
    rewrites: FAKE.UNDO_CONVERSION,
    steps: [
      { click: { css: '.series-hero [aria-label="More actions"]' } },
      { waitFor: '.mantine-Menu-dropdown' },
      { click: { css: '.mantine-Menu-dropdown *', text: 'Files' } },
      { wait: 500 },
      { click: { css: '.mantine-Menu-dropdown *', text: 'Undo conversion' } },
      { waitFor: '.mantine-Modal-content' },
    ],
    capture: { css: '.mantine-Modal-content' },
    pad: 0,
    note: 'Faked conversion history',
  },
  {
    name: 'series/release-search',
    path: (d) => `/series/${d.series.kaguya}`,
    rewrites: FAKE.RELEASE_SEARCH,
    steps: [{ click: 'Search releases' }, { waitFor: '.mantine-Modal-content' }, { wait: 1500 }],
    capture: { css: '.mantine-Modal-content' },
    pad: 0,
    note: 'Faked indexer results',
  },

  { name: 'reader/notes', path: reader, rewrites: FAKE.NOTES, settleMs: 1500, note: 'Faked notes from Maki and Rin' },
  { name: 'reader/notes-margin', path: reader, rewrites: FAKE.NOTES, settleMs: 1500, capture: { css: '.notes-margin' }, note: 'Faked notes' },
  { name: 'reader/note-composer', path: reader, rewrites: FAKE.NOTES, steps: [{ wait: 1200 }, { click: { css: '.notes-margin-add' } }, { waitFor: '.note-composer' }], capture: { css: '.notes-margin' } },

  { name: 'discover/shortlist', path: '/discover/shortlist', rewrites: FAKE.SHORTLIST, note: 'Faked: four saved titles' },
  { name: 'discover/shortlist-full', path: '/discover/shortlist', rewrites: FAKE.SHORTLIST, capture: 'full' },

  { name: 'settings/api-keys-list', path: '/settings?s=api-keys', rewrites: FAKE.API_KEYS, capture: { css: '#setting-api-keys' }, settleMs: 1800, note: 'Faked keys, one revoked' },
  { name: 'settings/updates-desktop', path: '/settings?s=updates', rewrites: FAKE.UPDATE_DESKTOP, capture: { css: '#setting-updates' }, settleMs: 1800, note: 'Faked: Windows app, update ready' },
  { name: 'settings/updates-docker', path: '/settings?s=updates', rewrites: FAKE.UPDATE_DOCKER, capture: { css: '#setting-updates' }, settleMs: 1800, note: 'Faked: Docker, update available' },
  { name: 'settings/opds-on', path: '/settings?s=opds', rewrites: FAKE.OPDS, capture: { css: '#setting-opds' }, settleMs: 1800, note: 'Faked: OPDS and Komga on' },
  { name: 'settings/koreader-sync-on', path: '/settings?s=koreader-sync', rewrites: FAKE.OPDS, capture: { css: '#setting-koreader-sync' }, settleMs: 1800, note: 'Faked: OPDS on' },
  { name: 'settings/kavita-connected', path: '/settings?s=kavita', rewrites: FAKE.KAVITA, capture: { css: '#setting-kavita' }, settleMs: 1800, note: 'Faked Kavita connection' },
  { name: 'settings/kavita-sync', path: '/settings?s=kavita-sync', rewrites: [...FAKE.KAVITA_SYNC, ...FAKE.KAVITA], capture: { css: '#setting-kavita-sync' }, settleMs: 1800, note: 'Faked Kavita reading' },
  { name: 'settings/page-conversion-running', path: '/settings?s=page-conversion', rewrites: FAKE.PAGE_CONVERSION_RUNNING, capture: { css: '#setting-page-conversion' }, settleMs: 1800, note: 'Faked: library pass running' },
  {
    name: 'settings/recycle-bin-window',
    path: '/settings?s=recycle-bin',
    rewrites: FAKE.RECYCLE_BIN,
    steps: [{ wait: 1500 }, { click: 'Open recycle bin' }, { waitFor: '.mantine-Modal-content' }, { wait: 600 }],
    capture: { css: '.mantine-Modal-content' },
    pad: 0,
    note: 'Faked bin contents',
  },
  {
    name: 'settings/two-factor-setup',
    path: '/settings?s=sign-in',
    rewrites: FAKE.TWO_FACTOR,
    steps: [{ wait: 1500 }, { click: { css: '#setting-sign-in button[aria-expanded]', text: 'Two-factor authentication' } }, { wait: 800 }, { click: { css: '#setting-sign-in button', text: 'Set up' } }, { wait: 1200 }],
    capture: { css: '#setting-sign-in' },
    note: 'Faked setup key',
  },

  { name: 'first-run/setup-guide', path: '/home', rewrites: FAKE.SETUP_GUIDE, steps: [{ waitFor: '.setup-modal' }, { wait: 800 }] },
  ...['Library', 'Downloads', 'Connections'].map((step) => ({
    name: `first-run/setup-${step.toLowerCase()}`,
    path: '/home',
    rewrites: FAKE.SETUP_GUIDE,
    steps: [{ waitFor: '.setup-modal' }, { click: { css: '.setup-rail *', text: step } }, { wait: 1000 }],
  })),
  { name: 'first-run/metadata-gate', path: '/home', rewrites: FAKE.METADATA_GATE, settleMs: 1500, note: 'Faked: catalogue still downloading' },
  { name: 'first-run/create-account', path: '/', signedOut: true, rewrites: FAKE.FIRST_ACCOUNT, settleMs: 1500, note: 'Faked: a fresh install' },
  { name: 'first-run/sign-in', path: '/login', signedOut: true, settleMs: 1500 },

  {
    name: 'import/scan',
    path: '/import',
    rewrites: FAKE.IMPORT,
    steps: [{ click: { css: 'input[placeholder="Root folder"]' } }, { waitFor: '[role=option]' }, { click: { css: '[role=option]' } }, { click: { css: 'button', text: 'Scan' } }, { wait: 1500 }],
    capture: 'full',
    note: 'Faked scan of eight folders',
  },
  {
    name: 'import/preview',
    path: '/import',
    rewrites: FAKE.IMPORT,
    steps: [{ click: { css: 'input[placeholder="Root folder"]' } }, { waitFor: '[role=option]' }, { click: { css: '[role=option]' } }, { click: { css: 'button', text: 'Scan' } }, { wait: 1500 }, { click: { css: 'button', text: 'Preview' } }, { waitFor: '.mantine-Modal-content' }, { wait: 1500 }],
    capture: { css: '.mantine-Modal-content' },
    pad: 0,
    note: 'Faked import plan',
  },
  { name: 'import/recent', path: '/import', rewrites: FAKE.IMPORT, capture: { rail: 'Recent imports' }, note: 'Faked batches' },
  {
    name: 'import/mihon-review',
    path: '/import',
    rewrites: FAKE.MIHON,
    steps: [
      { click: 'Mihon backup' },
      { upload: fileURLToPath(new URL('./fixtures/backup.tachibk', import.meta.url)), into: 'input[type=file]' },
      { wait: 500 },
      { click: { css: 'button', text: 'Read backup' } },
      { wait: 2500 },
    ],
    capture: 'full',
    note: 'Faked backup contents',
  },

  // Screen-height versions of tall pages, for the feature overviews
  { name: 'discover/taste-top', path: '/discover/taste' },
  { name: 'discover/taste-how-you-read', path: '/discover/taste', steps: [{ eval: () => { const h = [...document.querySelectorAll('h2, h3')].find((e) => e.textContent.trim().startsWith('How you read')); if (h) window.scrollTo(0, h.getBoundingClientRect().top + scrollY - 90) } }, { wait: 600 }] },
  { name: 'chapters/tab', path: frierenChapters, steps: [{ eval: () => { const h = [...document.querySelectorAll('h2, h3')].find((e) => e.textContent.trim().startsWith('Chapters')); if (h) window.scrollTo(0, h.getBoundingClientRect().top + scrollY - 90) } }, { wait: 600 }] },
  { name: 'pages/scrobble-top', path: '/scrobble' },
  { name: 'library/top', path: '/library', note: 'Grid with the summary counts and filter bar' },

  { name: 'pages/add-series-search', path: '/add?q=Mushishi' },
  { name: 'pages/import-mihon', path: '/import', steps: [{ click: 'Mihon backup' }] },
]
