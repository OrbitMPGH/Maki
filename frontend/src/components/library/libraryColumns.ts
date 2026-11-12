import { msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import type { ColumnSpec } from '../../lib/useColumnWidths'

/** Every column the Library table can show beside the cover and title, which are always there. */
export const LIBRARY_COLUMNS = [
  'status',
  'chapters',
  'unread',
  'added',
  'lastRead',
  'released',
  'size',
  'sources',
  'quality',
  'nextCheck',
  'year',
  'author',
  'score',
  'rating',
  'tags',
] as const

export type LibraryColumn = (typeof LIBRARY_COLUMNS)[number]

export const DEFAULT_LIBRARY_COLUMNS: readonly LibraryColumn[] = [
  'status',
  'chapters',
  'unread',
  'lastRead',
  'released',
  'size',
  'sources',
]

export const LIBRARY_COLUMN_LABELS: Record<LibraryColumn, MessageDescriptor> = {
  status: msg`Status`,
  chapters: msg`Chapters`,
  unread: msg`Unread`,
  added: msg`Added`,
  lastRead: msg`Last read`,
  released: msg`Latest release`,
  size: msg`Size on disk`,
  sources: msg`Sources`,
  quality: msg`Quality profile`,
  nextCheck: msg`Next check`,
  year: msg`Year`,
  author: msg`Author`,
  score: msg`Community score`,
  rating: msg`My rating`,
  tags: msg`Tags`,
}

/**
 * The Library sort a header click picks. A column with no entry has no sort of its own and its
 * header is not a button; the sort control stays the one place sort state lives.
 */
export const LIBRARY_COLUMN_SORTS: Partial<Record<LibraryColumn | 'title', string>> = {
  title: 'title',
  status: 'status',
  chapters: 'chapters',
  unread: 'unread',
  added: 'added',
  lastRead: 'read',
  released: 'released',
  year: 'year',
  author: 'author',
  score: 'score',
  rating: 'rating',
}

/** Default and smallest width per column, in px. Title, set in LibraryTable, is the flexible one. */
export const LIBRARY_COLUMN_WIDTHS: Record<LibraryColumn, ColumnSpec> = {
  status: { width: 128, min: 96 },
  chapters: { width: 128, min: 96 },
  unread: { width: 84, min: 64 },
  added: { width: 104, min: 80 },
  lastRead: { width: 104, min: 80 },
  released: { width: 116, min: 88 },
  size: { width: 96, min: 72 },
  sources: { width: 180, min: 100 },
  quality: { width: 140, min: 100 },
  nextCheck: { width: 104, min: 80 },
  year: { width: 64, min: 52 },
  author: { width: 160, min: 90 },
  score: { width: 84, min: 64 },
  rating: { width: 84, min: 64 },
  tags: { width: 180, min: 100 },
}

const STORAGE_KEY = 'library-table-columns'

/** The shown columns in their order, as this browser last left them. */
export function readLibraryColumns(): LibraryColumn[] {
  try {
    const raw = localStorage.getItem(STORAGE_KEY)
    if (raw == null) return [...DEFAULT_LIBRARY_COLUMNS]
    const parsed: unknown = JSON.parse(raw)
    if (!Array.isArray(parsed)) return [...DEFAULT_LIBRARY_COLUMNS]
    const known = parsed.filter((c): c is LibraryColumn => (LIBRARY_COLUMNS as readonly unknown[]).includes(c))
    return [...new Set(known)]
  } catch {
    return [...DEFAULT_LIBRARY_COLUMNS]
  }
}

export function writeLibraryColumns(columns: readonly LibraryColumn[]) {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(columns))
  } catch {
    /* a lost preference is not an error */
  }
}
