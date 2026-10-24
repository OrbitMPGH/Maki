import { http, HttpResponse } from 'msw'
import type { LibrarySettings } from '../../../src/api/hooks'
import type {
  IgnoredImportFolder,
  ImportBatch,
  ImportPlan,
  ImportUndoOutcome,
} from '../../../src/api/libraryImport'
import type { MetadataSearchResult } from '../../../src/api/types'
import { api, daysAgo, hoursAgo } from '../api'
import { metadataHit, rootFolders } from './common'

/** The shape `/libraryimport/scan` answers with; the page keeps its own copy of it. */
export interface ScanCandidateFixture {
  folderName: string
  cleanedTitle: string
  comicCount: number
  recognizedCount: number
  matches: MetadataSearchResult[]
  existingSeriesId: number | null
}

export const starlitFerry = metadataHit('mb-30011', 'Starlit Ferry', 2023, 'Ongoing', 41)
export const mothOrchard = metadataHit('mb-30012', 'Moth Orchard', 2020, 'Completed', 88)

export const scanCandidates: ScanCandidateFixture[] = [
  {
    folderName: 'Starlit Ferry (2023) [Digital]',
    cleanedTitle: 'Starlit Ferry',
    comicCount: 41,
    recognizedCount: 41,
    matches: [starlitFerry, metadataHit('mb-30013', 'Starlit Ferry: Night Crossing', 2024, 'Hiatus', 9)],
    existingSeriesId: null,
  },
  {
    folderName: 'Moth Orchard',
    cleanedTitle: 'Moth Orchard',
    comicCount: 88,
    recognizedCount: 80,
    matches: [mothOrchard],
    existingSeriesId: null,
  },
  {
    folderName: 'Tidewater Almanac vol 1-8 (Maki Scans)',
    cleanedTitle: 'Tidewater Almanac',
    comicCount: 8,
    recognizedCount: 8,
    matches: [metadataHit('mb-30014', 'Tidewater Almanac', 2021, 'Completed', 8)],
    existingSeriesId: null,
  },
  {
    folderName: 'clockwork tea house',
    cleanedTitle: 'clockwork tea house',
    comicCount: 12,
    recognizedCount: 12,
    matches: [metadataHit('10007', 'Clockwork Tea House', 2021, 'Completed', 120)],
    existingSeriesId: 7,
  },
  {
    folderName: 'zz_unsorted_scans_2019',
    cleanedTitle: 'zz unsorted scans 2019',
    comicCount: 3,
    recognizedCount: 0,
    matches: [],
    existingSeriesId: null,
  },
]

export const ignoredFolders: IgnoredImportFolder[] = [
  { id: 1, folderName: 'Extras and bonus pages', createdAt: daysAgo(20) },
  { id: 2, folderName: '_unsorted', createdAt: daysAgo(3) },
  { id: 3, folderName: 'Backups from the old NAS, do not touch', createdAt: hoursAgo(30) },
]

export const libraryImportSettings: LibrarySettings = {
  writeComicInfo: true,
  folderNamingMode: 'rename',
  seriesFolderFormat: '{Series TitleYear}',
  chapterFormat: '{Series Title} - Ch.{Chapter}',
  renameImportedFiles: true,
}

export const importPlans = (items: { folderName: string; metadataProviderId: string }[]): ImportPlan[] =>
  items.map((i, index): ImportPlan => {
    if (index === 1) {
      return {
        folderName: i.folderName,
        error: 'The folder holds no readable comic files.',
        seriesTitle: null,
        existingSeriesId: null,
        folderAction: 'keep',
        targetFolderName: null,
        seriesFolderName: null,
        linkDeferred: false,
        files: null,
        skipped: null,
        writesCover: false,
      }
    }
    return {
      folderName: i.folderName,
      error: null,
      seriesTitle: 'Starlit Ferry',
      existingSeriesId: null,
      folderAction: 'rename',
      targetFolderName: 'Starlit Ferry (2023)',
      seriesFolderName: 'Starlit Ferry (2023)',
      linkDeferred: true,
      writesCover: true,
      files: [
        {
          name: 'Starlit Ferry - Ch.1.cbz',
          source: 'Ch 001.cbz',
          entry: null,
          kind: 'cbz',
          action: 'register',
          aside: null,
          size: 24_000_000,
          number: '1',
          volume: null,
          volumeEnd: null,
          chapters: null,
          unlinked: null,
          loneFile: false,
        },
        {
          name: 'Starlit Ferry - Ch.2.cbz',
          source: 'ch2.zip',
          entry: null,
          kind: 'zip',
          action: 'build',
          aside: null,
          size: 22_500_000,
          number: '2',
          volume: null,
          volumeEnd: null,
          chapters: null,
          unlinked: null,
          loneFile: false,
        },
        {
          name: 'Starlit Ferry - Vol.1-2.cbz',
          source: 'Vol 1-2 raw pages',
          entry: null,
          kind: 'looseImages',
          action: 'build',
          aside: null,
          size: 310_000_000,
          number: null,
          volume: 1,
          volumeEnd: 2,
          chapters: null,
          unlinked: null,
          loneFile: false,
        },
        {
          name: 'Starlit Ferry - Ch.9.cbz',
          source: 'broken.cbz',
          entry: null,
          kind: 'cbz',
          action: 'rebuildInPlace',
          aside: 'broken.cbz.bak',
          size: 19_000_000,
          number: '9',
          volume: null,
          volumeEnd: null,
          chapters: null,
          unlinked: 'noMatchingChapter',
          loneFile: false,
        },
        {
          name: 'Starlit Ferry - Special.pdf',
          source: 'omake.pdf',
          entry: null,
          kind: 'pdf',
          action: 'register',
          aside: null,
          size: 8_000_000,
          number: null,
          volume: null,
          volumeEnd: null,
          chapters: null,
          unlinked: null,
          loneFile: true,
        },
      ],
      skipped: [
        { name: 'Thumbs.db.cbz', reason: 'unreadable' },
        { name: 'Ch 003 (copy).cbz', reason: 'duplicate' },
      ],
    }
  })

export const importPlansLinked = (items: { folderName: string; metadataProviderId: string }[]): ImportPlan[] =>
  items.map((i) => ({
    folderName: i.folderName,
    error: null,
    seriesTitle: 'Clockwork Tea House',
    existingSeriesId: 7,
    folderAction: 'merge',
    targetFolderName: 'Clockwork Tea House (2021)',
    seriesFolderName: 'Clockwork Tea House (2021)',
    linkDeferred: false,
    writesCover: false,
    files: [
      {
        name: 'Clockwork Tea House - Ch.1.cbz',
        source: '.',
        entry: null,
        kind: 'cbz',
        action: 'useExisting',
        aside: null,
        size: 21_000_000,
        number: '1',
        volume: null,
        volumeEnd: null,
        chapters: ['1'],
        unlinked: null,
        loneFile: false,
      },
      {
        name: 'Clockwork Tea House - Ch.2-14.cbz',
        source: '.',
        entry: null,
        kind: 'cbz',
        action: 'register',
        aside: null,
        size: 190_000_000,
        number: '2',
        volume: null,
        volumeEnd: null,
        chapters: Array.from({ length: 13 }, (_, n) => String(n + 2)),
        unlinked: null,
        loneFile: false,
      },
    ],
    skipped: [{ name: 'Ch 15.cbz', reason: 'leftBehind' }],
  }))

export const batches: ImportBatch[] = [
  {
    batchId: 'batch-a',
    rootFolderId: 1,
    createdAt: hoursAgo(5),
    folders: [
      {
        id: 11,
        originalFolderName: 'Starlit Ferry (2023) [Digital]',
        folderName: 'Starlit Ferry (2023)',
        seriesId: 13,
        seriesTitle: 'Starlit Ferry',
        createdSeries: true,
        folderAction: 'rename',
        fileCount: 41,
        builtCount: 3,
        undoneAt: null,
        linkPending: true,
        seriesGone: false,
        diskPending: false,
      },
      {
        id: 12,
        originalFolderName: 'clockwork tea house',
        folderName: 'Clockwork Tea House (2021)',
        seriesId: 7,
        seriesTitle: 'Clockwork Tea House',
        createdSeries: false,
        folderAction: 'merge',
        fileCount: 12,
        builtCount: 0,
        undoneAt: null,
        linkPending: false,
        seriesGone: false,
        diskPending: false,
      },
      {
        id: 13,
        originalFolderName: 'Orchard Moths [Retail]',
        folderName: 'Orchard Moths',
        seriesId: 14,
        seriesTitle: 'Orchard Moths',
        createdSeries: true,
        folderAction: 'keep',
        fileCount: 88,
        builtCount: 0,
        undoneAt: hoursAgo(4),
        linkPending: false,
        seriesGone: false,
        diskPending: true,
      },
    ],
  },
  {
    batchId: 'batch-b',
    rootFolderId: 1,
    createdAt: daysAgo(9),
    folders: [
      {
        id: 21,
        originalFolderName: 'Tidewater Almanac vol 1-8 (Maki Scans)',
        folderName: 'Tidewater Almanac (2021)',
        seriesId: null,
        seriesTitle: 'Tidewater Almanac',
        createdSeries: true,
        folderAction: 'rename',
        fileCount: 8,
        builtCount: 0,
        undoneAt: null,
        linkPending: false,
        seriesGone: true,
        diskPending: false,
      },
    ],
  },
]

export const importHandlers = [
  http.get(api('/rootfolder'), () => HttpResponse.json(rootFolders)),
  http.get(api('/settings/library'), () => HttpResponse.json(libraryImportSettings)),
  http.get(api('/libraryimport/scan'), () => HttpResponse.json(scanCandidates)),
  http.post(api('/libraryimport/import'), async ({ request }) => {
    const body = (await request.json()) as { items: { folderName: string }[] }
    return HttpResponse.json(
      body.items.map((i, index) =>
        index === 1
          ? {
              folderName: i.folderName,
              success: false,
              error: 'The folder is read-only, nothing was changed.',
              seriesId: null,
              newFolderName: null,
              filesLinked: 0,
              filesUnrecognized: 0,
              filesAdded: 0,
              linkPending: false,
            }
          : {
              folderName: i.folderName,
              success: true,
              error: null,
              seriesId: 13 + index,
              newFolderName: index === 0 ? 'Starlit Ferry (2023)' : 'Tidewater Almanac (2021)',
              filesLinked: 0,
              filesUnrecognized: 0,
              filesAdded: 41,
              linkPending: true,
              warnings: index === 0 ? ['Cover could not be downloaded, using the first page instead.'] : null,
              skipped: index === 0 ? [{ name: 'Ch 003 (copy).cbz', reason: 'duplicate' }] : null,
            },
      ),
    )
  }),
  http.post(api('/libraryimport/plan'), async ({ request }) => {
    const body = (await request.json()) as { items: { folderName: string; metadataProviderId: string }[] }
    return HttpResponse.json(importPlans(body.items))
  }),
  http.get(api('/libraryimport/ignored'), () => HttpResponse.json(ignoredFolders)),
  http.post(api('/libraryimport/ignored'), () => new HttpResponse(null, { status: 204 })),
  http.delete(api('/libraryimport/ignored/:id'), () => new HttpResponse(null, { status: 204 })),
  http.get(api('/libraryimport/batches'), () => HttpResponse.json(batches)),
  http.post(api('/libraryimport/batches/folders/:id/undo'), ({ params }) =>
    HttpResponse.json({
      id: Number(params.id),
      folderName: 'Starlit Ferry (2023) [Digital]',
      undone: true,
      error: null,
      warnings: null,
    } satisfies ImportUndoOutcome),
  ),
  http.post(api('/libraryimport/batches/:batchId/undo'), () =>
    HttpResponse.json([
      { id: 11, folderName: 'Starlit Ferry (2023) [Digital]', undone: true, error: null, warnings: null },
      {
        id: 12,
        folderName: 'clockwork tea house',
        undone: false,
        error: 'The series has been read since, so it cannot be undone.',
        warnings: null,
      },
    ] satisfies ImportUndoOutcome[]),
  ),
  http.get(api('/libraryimport/resolve'), ({ request }) => {
    const value = new URL(request.url).searchParams.get('value') ?? ''
    if (value.includes('nope')) {
      return HttpResponse.json(
        { code: 'error.import.unresolved', error: 'No series matches that id or link.' },
        { status: 404 },
      )
    }
    return HttpResponse.json(metadataHit('mb-30030', 'Paper Crane Cartel', 2019, 'Completed', 54))
  }),
  http.get(api('/search/metadata'), ({ request }) => {
    const query = (new URL(request.url).searchParams.get('query') ?? '').toLowerCase()
    if (query.includes('nothing')) return HttpResponse.json([])
    return HttpResponse.json([
      metadataHit('mb-30020', 'Paper Crane Cartel', 2019, 'Completed', 54),
      metadataHit('mb-30021', 'Paper Crane Cartel: Second Flight', 2022, 'Ongoing', 17),
      metadataHit('mb-30022', 'Cartel of the Crane Court', null, 'Hiatus'),
    ])
  }),
]
