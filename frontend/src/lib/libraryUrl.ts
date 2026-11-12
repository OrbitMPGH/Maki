import type { LibraryFilterSpec } from '../api/types'

type Key = keyof LibraryFilterSpec

type Field =
  | { key: Key; param?: string; kind: 'text' }
  | { key: Key; kind: 'enum'; values: readonly string[] }
  | { key: Key; kind: 'list'; values?: readonly string[] }
  | { key: Key; kind: 'ids' }
  | { key: Key; kind: 'flag' }
  | { key: Key; kind: 'int'; min: number; max: number }
  | { key: Key; kind: 'num'; min: number; max: number }

const ANY_ALL = ['any', 'all'] as const

const FIELDS: Field[] = [
  { key: 'query', param: 'q', kind: 'text' },
  { key: 'sort', kind: 'text' },
  { key: 'sortDir', kind: 'enum', values: ['asc', 'desc'] },
  { key: 'status', kind: 'text' },
  { key: 'types', kind: 'list' },
  { key: 'tagIds', kind: 'ids' },
  { key: 'tagMatch', kind: 'enum', values: ANY_ALL },
  { key: 'excludeTagIds', kind: 'ids' },
  { key: 'untaggedOnly', kind: 'flag' },
  { key: 'genres', kind: 'list' },
  { key: 'genreMatch', kind: 'enum', values: ANY_ALL },
  { key: 'excludeGenres', kind: 'list' },
  { key: 'metadataTags', kind: 'list' },
  { key: 'metadataTagMatch', kind: 'enum', values: ANY_ALL },
  { key: 'contentRatings', kind: 'list' },
  { key: 'readingStatuses', kind: 'list' },
  { key: 'monitored', kind: 'enum', values: ['all', 'monitored', 'unmonitored'] },
  { key: 'completeness', kind: 'enum', values: ['all', 'behind', 'complete'] },
  { key: 'sources', kind: 'list' },
  { key: 'sourceMatch', kind: 'enum', values: ANY_ALL },
  { key: 'sourceState', kind: 'enum', values: ['all', 'none', 'hasDisabled', 'noneEnabled', 'hasEnabled'] },
  { key: 'fileSources', kind: 'list' },
  { key: 'fileSourceMatch', kind: 'enum', values: ANY_ALL },
  { key: 'qualityProfile', kind: 'text' },
  { key: 'readMin', kind: 'int', min: 0, max: 100 },
  { key: 'readMax', kind: 'int', min: 0, max: 100 },
  { key: 'chapterMin', kind: 'int', min: 0, max: 100000 },
  { key: 'chapterMax', kind: 'int', min: 0, max: 100000 },
  { key: 'chapterMode', kind: 'enum', values: ['downloaded', 'total'] },
  { key: 'ratingState', kind: 'enum', values: ['all', 'rated', 'unrated'] },
  { key: 'ratingMin', kind: 'int', min: 1, max: 10 },
  { key: 'ratingMax', kind: 'int', min: 1, max: 10 },
  { key: 'communityRatingMin', kind: 'num', min: 0, max: 10 },
  { key: 'communityRatingMax', kind: 'num', min: 0, max: 10 },
  { key: 'yearMin', kind: 'int', min: 0, max: 9999 },
  { key: 'yearMax', kind: 'int', min: 0, max: 9999 },
  { key: 'credits', kind: 'list' },
  { key: 'publishers', kind: 'list' },
  { key: 'rootFolderIds', kind: 'ids' },
  { key: 'incognito', kind: 'enum', values: ['all', 'on', 'off'] },
  { key: 'muted', kind: 'enum', values: ['all', 'muted', 'unmuted'] },
  { key: 'anime', kind: 'enum', values: ['all', 'has', 'none'] },
  { key: 'numberingClash', kind: 'enum', values: ['all', 'clash', 'none'] },
  { key: 'overrides', kind: 'list', values: ['readCleanup', 'pageConversion'] },
  { key: 'addedWithinDays', kind: 'int', min: 1, max: 100000 },
]

/** Pairs where an inverted window means the link was edited by hand; both ends are dropped. */
const WINDOWS: [Key, Key][] = [
  ['readMin', 'readMax'],
  ['chapterMin', 'chapterMax'],
  ['ratingMin', 'ratingMax'],
  ['communityRatingMin', 'communityRatingMax'],
  ['yearMin', 'yearMax'],
]

const paramOf = (f: Field) => (f.kind === 'text' && f.param) || f.key

/** The values a field carries as strings, empty for "unset", so a value and its default compare. */
function texts(value: unknown): string[] {
  if (value == null || value === '' || value === false) return []
  if (Array.isArray(value)) return value.map(String).filter((v) => v !== '')
  return [String(value)]
}

/**
 * The query string for a view: only what differs from `defaults`, in a fixed order so the same
 * view always produces the same string. Lists repeat the parameter, since a publisher name can
 * hold a comma.
 */
export function specToParams(spec: LibraryFilterSpec, defaults: LibraryFilterSpec): URLSearchParams {
  const params = new URLSearchParams()
  for (const f of FIELDS) {
    const value = texts(spec[f.key])
    const base = texts(defaults[f.key])
    if (value.length === base.length && value.every((v, i) => v === base[i])) continue
    const name = paramOf(f)
    if (f.kind === 'flag') params.append(name, '1')
    else for (const v of value) params.append(name, f.kind === 'text' ? v.trim() : v)
  }
  return params
}

/**
 * The parts of a view a link asks for. Unknown parameters, values of the wrong shape and values out
 * of range are ignored, so a stale or hand-edited link still opens. Null means nothing usable was
 * in the link.
 */
export function paramsToSpec(params: URLSearchParams, sorts: readonly string[]): Partial<LibraryFilterSpec> | null {
  const out: Record<string, unknown> = {}
  for (const f of FIELDS) {
    const raw = params.getAll(paramOf(f)).map((v) => v.trim()).filter((v) => v !== '')
    if (raw.length === 0) continue
    const first = raw[0]
    switch (f.kind) {
      case 'text':
        if (f.key !== 'sort' || sorts.includes(first)) out[f.key] = first
        break
      case 'enum':
        if (f.values.includes(first)) out[f.key] = first
        break
      case 'list': {
        const values = [...new Set(f.values ? raw.filter((v) => f.values!.includes(v)) : raw)]
        if (values.length > 0) out[f.key] = values
        break
      }
      case 'ids': {
        const ids = [...new Set(raw.filter((v) => /^\d+$/.test(v)).map(Number))]
        if (ids.length > 0) out[f.key] = ids
        break
      }
      case 'flag':
        if (first === '1') out[f.key] = true
        break
      case 'int': {
        const n = Number(first)
        if (/^-?\d+$/.test(first) && n >= f.min && n <= f.max) out[f.key] = n
        break
      }
      case 'num': {
        const n = Number(first)
        if (Number.isFinite(n) && n >= f.min && n <= f.max) out[f.key] = n
        break
      }
    }
  }
  for (const [lo, hi] of WINDOWS) {
    const a = out[lo]
    const b = out[hi]
    if (typeof a === 'number' && typeof b === 'number' && a > b) {
      delete out[lo]
      delete out[hi]
    }
  }
  return Object.keys(out).length > 0 ? (out as Partial<LibraryFilterSpec>) : null
}

/** The query parameter for the grid, list or table view. Not part of a saved filter. */
export const VIEW_PARAM = 'view'

/** The view a link asks for, or null when it names none this page offers. */
export function viewFromParams<V extends string>(params: URLSearchParams, views: readonly V[]): V | null {
  const value = params.get(VIEW_PARAM)?.trim()
  return value != null && (views as readonly string[]).includes(value) ? (value as V) : null
}
