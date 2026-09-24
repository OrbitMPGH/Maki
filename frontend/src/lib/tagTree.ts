import type { TagOption } from '../api/hooks'

export interface TagNode {
  name: string
  /** Full path including this node, joined with " > " and lower-cased. */
  key: string
  /** Ancestors joined with " > ", empty at a root. */
  path: string
  /** Undefined for synthetic nodes. */
  count?: number
  /** Own count plus every descendant's, for ordering siblings. */
  total: number
  hasSubtags: boolean
  /** A path segment with no tag of its own, so it can be browsed into but not added. */
  synthetic: boolean
  children: TagNode[]
}

export const PATH_SEPARATOR = ' > '

/** The tree key for an option: its full path, lower-cased. */
export const tagKey = (option: Pick<TagOption, 'name' | 'path'>) =>
  (option.path ? `${option.path}${PATH_SEPARATOR}${option.name}` : option.name).toLowerCase()

function compareNodes(a: TagNode, b: TagNode) {
  return b.total - a.total || a.name.localeCompare(b.name)
}

/** Builds the tag tree keyed by full path, adding synthetic nodes for segments no option names. */
export function buildTagTree(options: TagOption[]): TagNode[] {
  const byKey = new Map<string, TagNode>()
  const roots: TagNode[] = []

  const ensure = (segments: string[]): TagNode => {
    const key = segments.join(PATH_SEPARATOR).toLowerCase()
    const existing = byKey.get(key)
    if (existing) return existing
    const node: TagNode = {
      name: segments[segments.length - 1],
      key,
      path: segments.slice(0, -1).join(PATH_SEPARATOR),
      total: 0,
      hasSubtags: false,
      synthetic: true,
      children: [],
    }
    byKey.set(key, node)
    if (segments.length === 1) roots.push(node)
    else ensure(segments.slice(0, -1)).children.push(node)
    return node
  }

  for (const option of options) {
    const segments = option.path ? option.path.split(PATH_SEPARATOR) : []
    const node = ensure([...segments, option.name])
    if (!node.synthetic) continue
    node.count = option.count
    node.hasSubtags = option.hasSubtags
    node.synthetic = false
  }

  const settle = (nodes: TagNode[]) => {
    for (const node of nodes) {
      settle(node.children)
      node.total = (node.count ?? 0) + node.children.reduce((sum, child) => sum + child.total, 0)
    }
    nodes.sort(compareNodes)
  }
  settle(roots)
  return roots
}

/**
 * Tags whose name or any path segment contains the needle: name-prefix matches first, then
 * name-contains, then path-only, each by count. `needle` must already be trimmed and lower-cased.
 */
export function rankTagMatches(options: TagOption[], needle: string, limit: number, skip?: (o: TagOption) => boolean) {
  const buckets: TagOption[][] = [[], [], []]
  for (const option of options) {
    if (skip?.(option)) continue
    const name = option.name.toLowerCase()
    if (name.startsWith(needle)) buckets[0].push(option)
    else if (name.includes(needle)) buckets[1].push(option)
    else if (option.path.toLowerCase().includes(needle)) buckets[2].push(option)
  }
  const all = buckets.flatMap((b) => b.sort((a, b2) => b2.count - a.count))
  return { total: all.length, items: all.slice(0, limit) }
}
