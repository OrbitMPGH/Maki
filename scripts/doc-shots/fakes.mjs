// Made-up API answers for states the showcase can't reach on its own. Each export is a list of
// rewrites for a shot's `rewrites`: { match, apply } edits the server's answer, { match, respond }
// replaces it, and `method` (default GET) lets a write return something too.

const ago = (minutes) => new Date(Date.now() - minutes * 60000).toISOString()
const DAY = 60 * 24
const ME = { id: 1, name: 'Maki' }
const RIN = { id: 2, name: 'Rin' }
const json = (re) => new RegExp(re)
const seriesId = (d) => Number(d.url.pathname.match(/\/series\/(\d+)/)?.[1])

// ---- Series page ----

export const seriesFields = (fields) => [{ match: /^\/api\/v1\/series\/\d+$/, apply: (b) => ({ ...b, ...fields }) }]

export const RATING_CHECKPOINT = seriesFields({ rating: null, finished: false, rateHintEligible: false, ratingCheckpoint: 'hooked', provisionalRating: 7 })
export const RATE_HINT = seriesFields({ rating: null, rateHintEligible: true, ratingCheckpoint: null })
export const ARCHIVE_HINT = seriesFields({ archiveHintEligible: true })
export const ARCHIVE_DIALOG = seriesFields({ rating: null, finished: false, provisionalRating: 8 })

export const RATING_HISTORY = [
  {
    match: /^\/api\/v1\/series\/\d+\/rating-history$/,
    respond: () => [
      { rating: 8, final: false, atChapter: 34, origin: 'Checkpoint', createdAt: ago(40 * DAY) },
      { rating: 9, final: true, atChapter: 62, origin: 'Finish', createdAt: ago(3 * DAY) },
    ],
  },
]

export const NEAR_MISS = [
  {
    match: /^\/api\/v1\/sourcemapping\/nearmisses\?/,
    respond: () => [
      {
        id: 1,
        sourceName: 'mangapill',
        sourceSeriesId: 'sousou-no-frieren',
        title: 'Sousou no Frieren',
        url: 'https://mangapill.com/manga/sousou-no-frieren',
        coverUrl: null,
        author: 'Kanehito Yamada, Tsukasa Abe',
        language: 'en',
        description: 'After the party defeats the Demon King, the elf mage Frieren outlives her companions and sets out to understand them.',
        score: 0.52,
      },
    ],
  },
]

export const UNDO_CONVERSION = [
  {
    match: /^\/api\/v1\/series\/\d+\/pageconversion\/history$/,
    respond: () => ({ convertedFiles: 64, savedBytes: 3_221_225_472, restorableFiles: 48, restorableBytes: 2_684_354_560, checkedWith: ['webp:high', 'avif:balanced+png'] }),
  },
]

function releases(d) {
  const P = (vs, ve, score, verdict, reasons, up, met, miss, matched = true) => ({
    span: { volumeStart: vs, volumeEnd: ve, chapters: [], wholeSeries: false },
    tier: 'volume',
    score,
    verdict,
    reasons,
    upgradeCount: up,
    alreadyMetCount: met,
    missingCount: miss,
    titleMatched: matched,
    rivalTitle: null,
  })
  const r = (guid, title, size, indexer, seeders, leechers, parsed) => ({ guid, title, size, indexer, seeders, leechers, protocol: 'torrent', infoUrl: null, parsed })
  return {
    query: 'Kaguya-sama Love is War',
    tried: ['Kaguya-sama Love is War', 'Kaguya-sama wa Kokurasetai'],
    minSeeders: 3,
    releases: [
      r('r1', 'Kaguya-sama - Love Is War v01-07 (2017-2018) (Digital) (1r0n)', 1_620_000_000, 'Nyaa', 64, 2, P(1, 7, 214, 'proposal', ['adds_missing_chapters'], 58, 6, 4)),
      r('r2', 'Kaguya-sama - Love Is War v08 (2018) (Digital) (danke-Empire)', 241_000_000, 'Nyaa', 22, 0, P(8, 8, 188, 'autoGrab', [], 10, 0, 0)),
      r('r3', 'Kaguya-sama - Love Is War v09-v12 (Digital) (1r0n)', 980_000_000, 'Prowlarr: AnimeTosho', 9, 1, P(9, 12, 176, 'proposal', ['over_budget'], 40, 0, 0)),
      r('r4', 'Kaguya-sama - Love Is War v26 (2022) (Digital) (LuCaZ)', 205_000_000, 'Nyaa', 1, 0, P(26, 26, 150, 'ignore', [], 0, 10, 0)),
      r('r5', 'Kaguya Wants to be Confessed To Official Doujin v01 (Digital)', 160_000_000, 'Nyaa', 15, 0, P(1, 1, 0, 'ignore', ['title_related_series'], 0, 0, 0, false)),
    ],
    seriesId: d.series.kaguya,
  }
}
export const RELEASE_SEARCH = [{ match: /^\/api\/v1\/release\?/, respond: (d) => releases(d) }]

function compareSnapshot(d) {
  const F = d.series.frieren
  const ch = d.chapters.reader
  const pg = (i, w) => ({ url: `/api/v1/reader/chapter/${ch}/page/${i}`, width: w, height: Math.round(w * 1.5), bytes: 380_000 + i * 20_000 })
  const q = (tier, score, isUpgrade, w, rp, cp, bpp) => ({
    tier,
    score,
    matchedFormats: [],
    isUpgrade,
    reason: null,
    medianWidth: w,
    pageCount: 18,
    currentTier: 'aggregator',
    currentScore: 31,
    currentWidth: 800,
    resolutionPoints: rp,
    compressionPoints: cp,
    bitsPerPixel: bpp,
    currentResolutionPoints: 20,
    currentCompressionPoints: 11,
    currentBitsPerPixel: 0.8,
  })
  const panel = (mappingId, sourceName, displayName, w, quality, pages) => ({
    mappingId,
    sourceName,
    displayName,
    status: 'ready',
    error: null,
    chapterLabel: 'Ch. 63',
    aligned: true,
    pages,
    pageCount: 18,
    quality,
  })
  return {
    seriesId: F,
    running: false,
    mixedChapters: false,
    pagesAligned: true,
    chapterNumber: 63,
    commonChapters: [61, 62, 63],
    panels: [
      panel(1, 'mangadex', 'MangaDex', 1600, q('scanlator', 94, true, 1600, 60, 34, 1.8), [pg(1, 1600), pg(2, 1600), pg(3, 1600)]),
      panel(2, 'weebcentral', 'Weeb Central', 1100, q('aggregator', 58, true, 1100, 38, 20, 1.2), [pg(1, 1100), pg(2, 1100), pg(3, 1100)]),
      panel(3, 'atsumaru', 'Atsumaru', 800, q('aggregator', 30, false, 800, 20, 10, 0.8), [pg(1, 800), null, pg(3, 800)]),
    ],
  }
}
export const COMPARE = [
  { method: 'POST', match: /^\/api\/v1\/sourcemapping\/compare$/, respond: (d) => compareSnapshot(d) },
  { match: /^\/api\/v1\/sourcemapping\/compare\?/, respond: (d) => compareSnapshot(d) },
]

// The Link a source search asks the site itself, so it is answered here instead.
export const SOURCE_SEARCH = [
  {
    match: /^\/api\/v1\/search\/source\?/,
    respond: (d) => {
      const cover = `/api/v1/mediacover/${d.series.frieren}/cover.jpg`
      const r = (id, title, description) => ({ sourceSeriesId: id, title, url: `https://example.org/series/${id}`, coverUrl: cover, description })
      return [
        r('frieren-beyond-journeys-end', "Frieren: Beyond Journey's End", 'The adventure is over but life goes on for an elf mage just beginning to learn what living is all about.'),
        r('sousou-no-frieren', 'Sousou no Frieren', 'Raw chapters of Frieren at the Funeral.'),
        r('frieren-official-guidebook', "Frieren: Beyond Journey's End Official Guidebook", null),
      ]
    },
  },
]

// ---- Reader ----

export const NOTES = [
  {
    match: /^\/api\/v1\/chapters\/\d+\/notes$/,
    respond: (d) => {
      const chapterId = Number(d.url.pathname.match(/chapters\/(\d+)/)[1])
      const note = (id, extra) => ({ id, chapterId, parentId: null, editedAt: null, deleted: null, ...extra })
      return [
        note(101, {
          pageIndex: 0,
          visibility: 'shared',
          body: 'The opening panel mirrors the last page of chapter 1. Same field, fifty years later.',
          author: ME,
          isMine: true,
          canEdit: true,
          canDelete: true,
          createdAt: ago(26 * 60),
          replies: [
            note(102, { parentId: 101, pageIndex: null, visibility: 'shared', body: 'Noticed that too. Those are the flowers Himmel picked.', author: RIN, isMine: false, canEdit: false, canDelete: false, createdAt: ago(20 * 60) }),
          ],
        }),
        note(103, { pageIndex: 0, visibility: 'private', body: 'Reread this before the next anime episode.', author: ME, isMine: true, canEdit: true, canDelete: true, createdAt: ago(300) }),
        note(104, {
          pageIndex: 0,
          visibility: 'shared',
          body: "Watch Fern's hands here. ||She already knows Frieren is holding back her mana.||",
          author: RIN,
          isMine: false,
          canEdit: false,
          canDelete: false,
          createdAt: ago(90),
          replies: [],
        }),
      ]
    },
  },
  { match: /^\/api\/v1\/reader\/chapter\/\d+$/, apply: (b) => ({ ...b, noteCount: 3, notedPages: [0] }) },
]

// ---- Discover ----

export const HIDDEN_EVERYWHERE = [
  {
    match: /^\/api\/v1\/recommendations\/discover\/hidden$/,
    respond: () => ({ terms: [{ kind: 'genre', name: 'Horror' }, { kind: 'genre', name: 'Ecchi' }, { kind: 'tag', name: 'Gore' }] }),
  },
]

// Real catalogue entries, so the cards carry their own covers.
const SHORTLISTED = [
  { id: 222, savedFrom: 'rail:trending', days: 70, statusChange: { from: 'Ongoing', to: 'Hiatus' }, newChapters: 12 },
  { id: 2238, note: 'Recommended by a friend. The art school arc is the best part.', savedFrom: 'creator', savedFromDetail: 'Tsubasa Yamaguchi', days: 200 },
  { id: 3347, savedFrom: 'search', days: 20 },
  { id: 6886, savedFrom: 'importlist', savedFromDetail: 'AniList', days: 5, newChapters: 3 },
]

async function shortlist(d) {
  const entries = []
  for (const s of SHORTLISTED) {
    // A title the content rating hides answers 403; leave it out rather than fail the shot.
    const detail = await d.api(`/recommendations/detail/${s.id}`).catch(() => null)
    if (!detail) continue
    const item = {
      providerId: String(s.id),
      title: detail.title,
      coverUrl: detail.coverUrl ?? null,
      thumbUrl: detail.thumbUrl ?? detail.coverUrl ?? null,
      thumbUrlHiDpi: detail.thumbUrlHiDpi ?? null,
      year: detail.year ?? null,
      description: detail.description ?? null,
      status: detail.status ?? null,
      rating: detail.rating ?? null,
      totalChapters: detail.totalChapters ?? null,
      matchedGenres: (detail.genres ?? []).slice(0, 2),
      matchedTags: [],
      authorMatch: false,
      relationKind: null,
      relatedToTitle: null,
      becauseOfTitle: null,
    }
    entries.push({
      mangaBakaId: s.id,
      title: detail.title,
      item,
      type: 'manga',
      hasAnime: false,
      note: s.note ?? null,
      savedFrom: s.savedFrom,
      savedFromDetail: s.savedFromDetail ?? null,
      createdAtUtc: ago(s.days * DAY),
      statusChange: s.statusChange ?? null,
      newChapters: s.newChapters ?? null,
      library: null,
    })
  }
  return { catalogueAvailable: true, entries }
}
export const SHORTLIST = [
  { match: /^\/api\/v1\/shortlist$/, respond: (d) => shortlist(d) },
  { match: /^\/api\/v1\/shortlist\/ids$/, respond: () => ({ ids: SHORTLISTED.map((s) => s.id), count: SHORTLISTED.length }) },
]

// ---- Settings ----

export const API_KEYS = [
  {
    match: /^\/api\/v1\/account\/apikeys$/,
    respond: () => [
      { id: 1, name: 'Reading app', prefix: 'mk_a1b2', scope: 'Opds', createdAt: ago(60 * DAY), lastUsedAt: ago(120), revokedAt: null },
      { id: 2, name: 'Home automation', prefix: 'mk_c3d4', scope: 'Full', createdAt: ago(20 * DAY), lastUsedAt: null, revokedAt: null },
      { id: 3, name: 'Old laptop', prefix: 'mk_e5f6', scope: 'Full', createdAt: ago(300 * DAY), lastUsedAt: ago(120 * DAY), revokedAt: ago(100 * DAY) },
    ],
  },
]

const RELEASE_NOTES = "## What's new\n\n- Offline reading keeps your place\n- Faster library imports\n\n## Fixes\n\n- The reader no longer jumps on rotate"
const update = (d, extra) => ({
  currentVersion: (d.version ?? 'v0.37.0').replace(/^v/, ''),
  isDevBuild: false,
  isDocker: false,
  updateAvailable: true,
  latestVersion: '0.38.0',
  releaseUrl: 'https://github.com/OrbitMPGH/Maki/releases',
  releaseNotes: RELEASE_NOTES,
  checkedAt: ago(45),
  runtime: 'desktop',
  updateReady: true,
  ...extra,
})
export const UPDATE_DESKTOP = [{ match: /^\/api\/v1\/system\/update$/, respond: (d) => update(d, {}) }]
export const UPDATE_DOCKER = [{ match: /^\/api\/v1\/system\/update$/, respond: (d) => update(d, { isDocker: true, runtime: 'docker', updateReady: false }) }]

const OPDS_ON = { enabled: true, trackProgress: true, hasToken: true, tokenPrefix: 'f07be2', feedUrl: '/api/v1/opds/f07be2c19a44d85e7a30', komga: true }
export const OPDS = [{ match: /^\/api\/v1\/settings\/opds$/, apply: (b) => ({ ...b, ...OPDS_ON }) }]

export const KAVITA_SYNC = [{ match: /^\/api\/v1\/reader\/used$/, apply: (b) => ({ ...b, used: true, kavita: true }) }]

export const KAVITA = [
  {
    match: /^\/api\/v1\/settings\/kavita$/,
    apply: (b) => ({ ...b, url: 'http://192.168.1.20:5000', apiKey: 'kavita-example-key', pathMapFrom: '/data/manga', pathMapTo: '/manga', userId: 1, resolvedUserId: 1 }),
  },
  { match: /^\/api\/v1\/settings\/connection-tests$/, apply: (b) => [...(Array.isArray(b) ? b.filter((t) => t.key !== 'kavita') : []), { key: 'kavita', ok: true, message: null, testedAt: ago(12) }] },
  { match: /^\/api\/v1\/settings\/kavita\/libraries$/, respond: () => [{ id: 1, name: 'Manga' }, { id: 2, name: 'Webtoons' }] },
  { match: /^\/api\/v1\/settings\/scrobble$/, apply: (b) => ({ ...b, libraryIds: '1' }) },
]

export const PAGE_CONVERSION_RUNNING = [
  {
    match: /^\/api\/v1\/pageconversion$/,
    apply: (b) => ({
      ...b,
      settings: { ...(b.settings ?? {}), format: 'webp', stop: 'balanced', pngLossless: true },
      pass: {
        state: 'running',
        hold: null,
        currentFile: 'ONE PIECE (1997)/ONE PIECE Ch.12.cbz',
        totals: { startedAtUtc: ago(40), finishedAtUtc: null, converted: 118, unchanged: 9, hardlinked: 0, failed: 0, savedBytes: 1_717_986_918 },
      },
      library: { ...(b.library ?? {}), convertedFiles: 530, savedBytes: 11_811_160_064, pendingFiles: 1290, queuedFiles: 6, trashBytes: 6_549_825_126, trashFiles: 118, trashDays: 14 },
    }),
  },
]

export const RECYCLE_BIN = [
  {
    match: /^\/api\/v1\/recyclebin$/,
    respond: (d) => {
      const F = d.series.frieren
      const K = d.series.kaguya
      const O = d.series.onePiece
      const e = (id, sid, title, folder, file, size, reason, daysAgo, by, chapters) => ({
        id,
        seriesId: sid,
        seriesTitle: title,
        seriesExists: true,
        fileName: file,
        relativePath: `${folder}/${file}`,
        size,
        reason,
        deletedAt: ago(daysAgo * DAY),
        deletedBy: by,
        expiresAt: ago(-(14 - daysAgo) * DAY),
        missing: false,
        chapters,
      })
      const fr = "Frieren: Beyond Journey's End"
      const frDir = "Frieren Beyond Journey's End (2020)"
      return {
        retentionDays: 14,
        totalBytes: 262_500_000,
        entries: [
          e(1, F, fr, frDir, "Frieren Beyond Journey's End Ch.18.cbz", 31_000_000, 'readCleanup', 2, null, [{ number: 18, volume: null, language: 'en' }]),
          e(2, F, fr, frDir, "Frieren Beyond Journey's End Ch.19.cbz", 29_500_000, 'readCleanup', 2, null, [{ number: 19, volume: null, language: 'en' }]),
          e(3, K, 'Kaguya-sama: Love is War', 'Kaguya-sama Love is War (2016)', 'Kaguya-sama Love is War Vol.03.cbz', 142_000_000, 'removeChapter', 5, 'Maki', [
            { number: 21, volume: 3, language: 'en' },
            { number: 22, volume: 3, language: 'en' },
          ]),
          e(4, O, 'ONE PIECE', 'ONE PIECE (1997)', 'ONE PIECE Ch.12.cbz', 22_000_000, 'deleteFile', 10, 'Maki', [{ number: 12, volume: null, language: 'en' }]),
        ],
      }
    },
  },
  { match: /^\/api\/v1\/recyclebin\/retention$/, respond: () => ({ days: 14 }) },
]

export const TWO_FACTOR = [
  { match: /^\/api\/v1\/account\/2fa$/, respond: () => ({ enabled: false, hasAuthenticator: false, recoveryCodesLeft: 0, available: true, hasPassword: true, ssoDelegated: false }) },
  { method: 'POST', match: /^\/api\/v1\/account\/2fa\/setup$/, respond: () => ({ sharedKey: 'JBSW Y3DP EHPK 3PXP', authenticatorUri: 'otpauth://totp/Maki:maki?secret=JBSWY3DPEHPK3PXP&issuer=Maki' }) },
]

// ---- First run ----

export const SETUP_GUIDE = [{ match: /^\/api\/v1\/settings\/setup$/, apply: (b) => ({ ...b, completed: false }) }]

export const METADATA_GATE = [
  { match: /^\/api\/v1\/settings\/metadata$/, apply: (b) => ({ ...b, dumpPresent: false, dumpSizeBytes: null, dumpRefreshedAt: null, dumpDownloadFailed: false }) },
  {
    match: /^\/api\/v1\/settings\/metadata\/dump-progress$/,
    respond: () => ({
      running: true,
      phase: 'downloading',
      downloadedBytes: 1_153_433_600,
      totalBytes: 1_887_436_800,
      indexProgress: null,
      bytesPerSecond: 14_680_064,
      estimatedSecondsRemaining: 50,
      startedAt: ago(2),
      finishedAt: null,
      lastInstalled: false,
      lastError: null,
    }),
  },
]

export const FIRST_ACCOUNT = [{ match: /^\/initialize\.json$/, apply: (b) => ({ ...b, setupNeeded: true }) }]

// ---- Import ----

const hit = (id, title, year, status, ch) => ({ providerId: id, title, coverUrl: null, year, status, description: null, totalChapters: ch })
function scanCandidates(d) {
  const cand = (folderName, cleanedTitle, n, rec, matches, existingSeriesId = null, kind = 'new', extra = {}) => ({
    folderName,
    cleanedTitle,
    comicCount: n,
    recognizedCount: rec,
    matches,
    existingSeriesId,
    kind,
    matchSource: matches.length ? 'folder' : null,
    matchHint: null,
    ...extra,
  })
  return [
    cand('Blue Period (2017) [Digital]', 'Blue Period', 60, 60, [hit('2238', 'Blue Period', 2017, 'Ongoing', 160)]),
    cand('March Comes in Like a Lion v01-17', 'March Comes in Like a Lion', 17, 17, [hit('3347', 'March Comes in Like a Lion', 2007, 'Ongoing', 200)], null, 'new', { matchSource: 'comicInfo', matchHint: '3-gatsu no Lion' }),
    cand('apothecary diaries', 'apothecary diaries', 40, 38, [hit('222', 'The Apothecary Diaries', 2017, 'Ongoing', 80), hit('84242', 'The Apothecary Diaries (Novel)', 2011, 'Ongoing', 15)]),
    cand('Delicious in Dungeon [Yen Press]', 'Delicious in Dungeon', 97, 97, [hit('6886', 'Delicious in Dungeon', 2014, 'Completed', 97)]),
    cand('Kaguya-sama Love is War', 'Kaguya-sama Love is War', 28, 28, [hit('10236', 'Kaguya-sama: Love is War', 2015, 'Completed', 281)], d.series.kaguya, 'inLibrary', { matchSource: 'library' }),
    cand('ONE PIECE', 'ONE PIECE', 105, 105, [hit('10237', 'ONE PIECE', 1997, 'Ongoing', 1130)], d.series.onePiece, 'inLibrary', { matchSource: 'library' }),
    cand('zz_unsorted_scans_2019', 'zz unsorted scans 2019', 3, 0, []),
    cand('Misc oneshots', 'Misc oneshots', 5, 1, []),
  ]
}

function importPlans() {
  const f = (name, source, kind, action, size, number, extra = {}) => ({
    name,
    source,
    entry: null,
    kind,
    action,
    aside: null,
    size,
    number,
    volume: null,
    volumeEnd: null,
    chapters: null,
    unlinked: null,
    loneFile: false,
    ...extra,
  })
  const plan = (folderName, seriesTitle, folderAction, target, files, extra = {}) => ({
    folderName,
    error: null,
    seriesTitle,
    existingSeriesId: null,
    folderAction,
    targetFolderName: target,
    seriesFolderName: target,
    linkDeferred: true,
    writesCover: true,
    kind: 'new',
    skipped: null,
    files,
    ...extra,
  })
  return [
    plan('Blue Period (2017) [Digital]', 'Blue Period', 'rename', 'Blue Period (2017)', [
      f('Blue Period Ch.1.cbz', 'Blue Period - c001.cbz', 'cbz', 'register', 24_000_000, '1'),
      f('Blue Period Ch.2.cbz', 'Blue Period - c002.zip', 'zip', 'build', 22_500_000, '2'),
    ]),
    plan('apothecary diaries', 'The Apothecary Diaries', 'rename', 'The Apothecary Diaries (2017)', [
      f('The Apothecary Diaries Ch.9.cbz', 'broken.cbz', 'cbz', 'rebuildInPlace', 19_000_000, '9', { aside: 'broken.cbz.bak', unlinked: 'noMatchingChapter' }),
    ], { skipped: [{ name: 'Thumbs.db.cbz', reason: 'unreadable' }, { name: 'Ch 003 (copy).cbz', reason: 'duplicate' }] }),
    plan('Delicious in Dungeon [Yen Press]', 'Delicious in Dungeon', 'keep', 'Delicious in Dungeon [Yen Press]', [
      f('Delicious in Dungeon Vol.1.cbz', 'Vol 1.cbz', 'cbz', 'register', 180_000_000, null, { volume: 1, volumeEnd: 1 }),
    ]),
    plan('March Comes in Like a Lion v01-17', null, 'keep', null, null, { error: 'The folder holds no readable comic files.', linkDeferred: false, writesCover: false }),
  ]
}

function importBatches(d) {
  const bf = (id, orig, folderName, sid, seriesTitle, createdSeries, folderAction, fileCount, extra = {}) => ({
    id,
    originalFolderName: orig,
    folderName,
    seriesId: sid,
    seriesTitle,
    createdSeries,
    folderAction,
    fileCount,
    builtCount: 0,
    undoneAt: null,
    linkPending: false,
    seriesGone: false,
    diskPending: false,
    ...extra,
  })
  return [
    {
      batchId: 'b-recent',
      rootFolderId: 1,
      createdAt: ago(300),
      folders: [
        bf(11, 'Frieren (2020) [Digital]', "Frieren Beyond Journey's End (2020)", d.series.frieren, "Frieren: Beyond Journey's End", true, 'rename', 62, { builtCount: 3 }),
        bf(12, 'kaguya-sama extras', 'Kaguya-sama Love is War (2016)', d.series.kaguya, 'Kaguya-sama: Love is War', false, 'merge', 6),
      ],
    },
    {
      batchId: 'b-older',
      rootFolderId: 1,
      createdAt: ago(7 * DAY),
      folders: [bf(13, 'ONE PIECE [Retail]', 'ONE PIECE (1997)', d.series.onePiece, 'ONE PIECE', true, 'rename', 17, { undoneAt: ago(6 * DAY) })],
    },
  ]
}

export const IMPORT = [
  { match: /^\/api\/v1\/libraryimport\/scan\?/, respond: (d) => scanCandidates(d) },
  { method: 'POST', match: /^\/api\/v1\/libraryimport\/plan$/, respond: () => importPlans() },
  { match: /^\/api\/v1\/libraryimport\/batches/, respond: (d) => importBatches(d) },
]

function mihon(d) {
  const m = (index, title, author, extra = {}) => ({
    index,
    title,
    author,
    categories: ['Reading'],
    aniListId: null,
    malId: null,
    kitsuId: null,
    mangaBakaId: null,
    readNumbers: [],
    trackerLastRead: null,
    chapterCount: 0,
    readCount: 0,
    ...extra,
  })
  const upTo = (n) => Array.from({ length: n }, (_, i) => i + 1)
  return {
    parse: {
      skippedNonLibrary: 4,
      categoryCount: 3,
      manga: [
        m(0, 'Sousou no Frieren', 'Yamada Kanehito', { aniListId: 118586, readNumbers: upTo(25), chapterCount: 147, readCount: 25 }),
        m(1, 'Kaguya-sama wa Kokurasetai', 'Akasaka Aka', { malId: 90125, categories: ['Finished'], readNumbers: upTo(281), chapterCount: 281, readCount: 281 }),
        m(2, 'One Piece', 'Oda Eiichiro', { aniListId: 30013, chapterCount: 1130, readCount: 1100, readNumbers: upTo(1100) }),
        m(3, 'Blue Period', 'Yamaguchi Tsubasa', { mangaBakaId: '2238', chapterCount: 60, readCount: 40, readNumbers: upTo(40) }),
        m(4, 'Kusuriya no Hitorigoto', 'Natsu Hyuuga', { categories: [], chapterCount: 60, readCount: 12, readNumbers: upTo(12) }),
        m(5, 'Untitled Scan Group Oneshot', null, { categories: ['Plan to read'] }),
      ],
    },
    resolve: {
      results: [
        { index: 0, status: 'matched', matchedBy: 'anilist', match: hit('10101', "Frieren: Beyond Journey's End", 2020, 'Hiatus', 147), candidates: [], library: 'visible', seriesId: d.series.frieren },
        { index: 1, status: 'matched', matchedBy: 'mal', match: hit('10102', 'Kaguya-sama: Love is War', 2015, 'Completed', 281), candidates: [], library: 'visible', seriesId: d.series.kaguya },
        { index: 2, status: 'matched', matchedBy: 'anilist', match: hit('10103', 'ONE PIECE', 1997, 'Ongoing', 1130), candidates: [], library: 'visible', seriesId: d.series.onePiece },
        { index: 3, status: 'matched', matchedBy: 'mangabaka', match: hit('2238', 'Blue Period', 2017, 'Ongoing', 160), candidates: [], library: 'none', seriesId: null },
        {
          index: 4,
          status: 'needsReview',
          matchedBy: 'title',
          match: null,
          candidates: [hit('222', 'The Apothecary Diaries', 2017, 'Ongoing', 80), hit('84242', 'The Apothecary Diaries (Novel)', 2011, 'Ongoing', 15)],
          library: 'none',
          seriesId: null,
        },
        { index: 5, status: 'notFound', matchedBy: null, match: null, candidates: [], library: 'none', seriesId: null },
      ],
    },
  }
}

export const MIHON = [
  { method: 'POST', match: /^\/api\/v1\/mihonimport\/parse$/, respond: (d) => mihon(d).parse },
  { method: 'POST', match: /^\/api\/v1\/mihonimport\/resolve$/, respond: (d) => mihon(d).resolve },
]

export { json, seriesId }
