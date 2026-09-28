import { useCallback, useMemo, useState } from 'react'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import {
  ActionIcon,
  Anchor,
  Breadcrumbs,
  Button,
  Checkbox,
  CloseButton,
  Group,
  Loader,
  Modal,
  ScrollArea,
  Stack,
  Text,
  TextInput,
  UnstyledButton,
} from '@mantine/core'
import { useDebouncedValue } from '@mantine/hooks'
import { IconChevronRight, IconSearch } from '@tabler/icons-react'
import { useRecommendationTags, type CatalogueTerm } from '../api/hooks'
import { buildTagTree, PATH_SEPARATOR, rankTagMatches, tagKey, type TagNode } from '../lib/tagTree'
import { useGenreOptions } from './CatalogueFilters'

const SEARCH_LIMIT = 100
const BROWSE_LIMIT = 300
// A trail starting with this is inside the Genres group rather than the tag tree.
const GENRES_KEY = '\u0000genres'

interface Row {
  id: string
  kind: 'genre' | 'tag'
  name: string
  label: string
  count?: number
  path?: string
  hasSubtags: boolean
  addable: boolean
  children: number
}

const termKey = (kind: string, name: string) => `${kind}:${name.toLowerCase()}`

const nodeRow = (node: TagNode): Row => ({
  id: node.key,
  kind: 'tag',
  name: node.name,
  label: node.name,
  count: node.count,
  path: node.path,
  hasSubtags: node.hasSubtags,
  addable: !node.synthetic,
  children: node.children.length,
})

const genreRow = (g: { value: string; label: string }): Row => ({
  id: `genre:${g.value}`,
  kind: 'genre',
  name: g.value,
  label: g.label,
  hasSubtags: false,
  addable: true,
  children: 0,
})

/** Walks MangaBaka's tag tree and toggles terms in one picker's list. */
export function TagBrowserModal({
  opened,
  onClose,
  kinds,
  tone,
  value,
  onChange,
}: {
  opened: boolean
  onClose: () => void
  kinds: ('genre' | 'tag')[]
  tone: 'include' | 'exclude'
  value: CatalogueTerm[]
  onChange: (terms: CatalogueTerm[]) => void
}) {
  const { t } = useLingui()
  const { data: tagOptions, isLoading } = useRecommendationTags()
  const genreOptions = useGenreOptions()
  const withGenres = kinds.includes('genre')

  const [search, setSearch] = useState('')
  const [trail, setTrail] = useState<string[]>([])
  const [withSubtags, setWithSubtags] = useState(false)

  const tree = useMemo(() => buildTagTree(tagOptions ?? []), [tagOptions])
  const nodes = useMemo(() => {
    const map = new Map<string, TagNode>()
    const walk = (list: TagNode[]) => {
      for (const node of list) {
        map.set(node.key, node)
        walk(node.children)
      }
    }
    walk(tree)
    return map
  }, [tree])

  const picked = useMemo(() => new Set(value.map((term) => termKey(term.kind, term.name))), [value])
  const needle = search.trim().toLowerCase()
  const [debouncedNeedle] = useDebouncedValue(needle, 100)
  const inGenres = trail[0] === GENRES_KEY
  const current = inGenres ? undefined : nodes.get(trail[trail.length - 1] ?? '')

  const searchRows = useCallback(
    (n: string): { rows: Row[]; total: number } => {
      const genres = withGenres ? genreOptions.filter((g) => g.label.toLowerCase().includes(n)).map(genreRow) : []
      const tags = rankTagMatches(tagOptions ?? [], n, SEARCH_LIMIT)
      const tagRows = tags.items.flatMap((option) => {
        const node = nodes.get(tagKey(option))
        return node ? [nodeRow(node)] : []
      })
      return { rows: [...genres, ...tagRows], total: genres.length + tags.total }
    },
    [withGenres, genreOptions, tagOptions, nodes],
  )

  const searching = needle !== '' && debouncedNeedle !== ''
  const { rows, total } = useMemo((): { rows: Row[]; total: number } => {
    if (searching) return searchRows(debouncedNeedle)
    if (inGenres) return { rows: genreOptions.map(genreRow), total: genreOptions.length }
    const children = current ? current.children : tree
    return { rows: children.slice(0, BROWSE_LIMIT).map(nodeRow), total: children.length }
  }, [searching, searchRows, debouncedNeedle, genreOptions, inGenres, current, tree])

  const isPicked = (row: Row) => picked.has(termKey(row.kind, row.name))

  const toggle = (row: Row) => {
    if (!row.addable) return
    const key = termKey(row.kind, row.name)
    if (picked.has(key)) {
      onChange(value.filter((term) => termKey(term.kind, term.name) !== key))
      return
    }
    const term: CatalogueTerm = { kind: row.kind, name: row.name }
    if (row.kind === 'tag' && withSubtags && row.hasSubtags) term.subtags = true
    onChange([...value, term])
  }

  const openNode = (key: string) => {
    const segments = key.split(PATH_SEPARATOR)
    setTrail(segments.map((_, i) => segments.slice(0, i + 1).join(PATH_SEPARATOR)))
    setSearch('')
  }

  const close = () => {
    setSearch('')
    onClose()
  }

  const showRootGenres = withGenres && !searching && trail.length === 0
  const noTags = !isLoading && (tagOptions?.length ?? 0) === 0
  const shown = rows.length

  const crumbs = [
    <Anchor key="root" component="button" type="button" size="sm" onClick={() => setTrail([])}>
      {withGenres ? <Trans>Everything</Trans> : <Trans>All tags</Trans>}
    </Anchor>,
    ...trail.map((key, i) => {
      const label = key === GENRES_KEY ? t`Genres` : (nodes.get(key)?.name ?? key)
      return i === trail.length - 1 ? (
        <Text key={key} size="sm" fw={600}>
          {label}
        </Text>
      ) : (
        <Anchor key={key} component="button" type="button" size="sm" onClick={() => setTrail(trail.slice(0, i + 1))}>
          {label}
        </Anchor>
      )
    }),
  ]

  return (
    <Modal
      opened={opened}
      onClose={close}
      size="lg"
      withinPortal
      title={tone === 'include' ? t`Browse tags to include` : t`Browse tags to exclude`}
      closeButtonProps={{ 'aria-label': t`Close` }}
      // Every open Mantine modal closes on Escape from a window listener, so a browser opened from
      // another modal would close both. Marking focused elements opts them out; Escape is handled here.
      closeOnEscape={false}
      onFocus={(e) => e.target.setAttribute('data-mantine-stop-propagation', 'true')}
      onKeyDown={(e) => {
        if (e.key !== 'Escape' || e.nativeEvent.isComposing) return
        e.stopPropagation()
        close()
      }}
    >
      <Stack gap="sm">
        <TextInput
          data-autofocus
          leftSection={<IconSearch size={16} />}
          placeholder={withGenres ? t`Search genres, tags or groups like Character Traits` : t`Search tags or groups like Character Traits`}
          value={search}
          onChange={(e) => setSearch(e.currentTarget.value)}
          onKeyDown={(e) => {
            if (e.key !== 'Enter' || !needle) return
            e.preventDefault()
            const results = debouncedNeedle === needle ? rows : searchRows(needle).rows
            const first = results.find((row) => row.addable && !isPicked(row))
            if (first) {
              toggle(first)
              setSearch('')
            }
          }}
          rightSectionPointerEvents="all"
          rightSection={
            search ? <CloseButton size="sm" aria-label={t`Clear search`} onClick={() => setSearch('')} /> : undefined
          }
        />

        {!searching && <Breadcrumbs separatorMargin={6}>{crumbs}</Breadcrumbs>}

        <ScrollArea.Autosize mah="55vh" type="auto" offsetScrollbars>
          <Stack gap={2}>
            {showRootGenres && (
              <BrowseRow
                row={{ ...genreRow({ value: GENRES_KEY, label: t`Genres` }), addable: false, children: genreOptions.length }}
                tone={tone}
                picked={false}
                onToggle={() => {}}
                onOpen={() => setTrail([GENRES_KEY])}
              />
            )}
            {rows.map((row) => (
              <BrowseRow
                key={row.id}
                row={row}
                showPath={searching}
                tone={tone}
                picked={isPicked(row)}
                onToggle={() => toggle(row)}
                onOpen={() => openNode(row.id)}
              />
            ))}
            {isLoading && (
              <Group justify="center" py="md">
                <Loader size="sm" />
              </Group>
            )}
            {!isLoading && rows.length === 0 && !showRootGenres && (
              <Text size="sm" c="var(--ink-3)" ta="center" py="md">
                {noTags && !searching ? t`Tags appear once the recommendation index is built` : t`No matches`}
              </Text>
            )}
          </Stack>
        </ScrollArea.Autosize>

        {total > shown && (
          <Text size="xs" c="var(--ink-3)">
            <Plural
              value={shown}
              one="Showing the first # result. Search to narrow it down."
              other="Showing the first # results. Search to narrow it down."
            />
          </Text>
        )}

        <Group justify="space-between" wrap="wrap" gap="sm" pt="sm" className="tag-browser-footer">
          <Checkbox
            size="xs"
            label={t`Include subtags when adding`}
            checked={withSubtags}
            onChange={(e) => setWithSubtags(e.currentTarget.checked)}
          />
          <Group gap="sm" wrap="nowrap">
            <Text size="sm" c="var(--ink-3)">
              <Plural value={value.length} one="# selected" other="# selected" />
            </Text>
            <Button size="xs" variant="default" onClick={close}>
              <Trans>Done</Trans>
            </Button>
          </Group>
        </Group>
      </Stack>
    </Modal>
  )
}

function BrowseRow({
  row,
  showPath,
  tone,
  picked,
  onToggle,
  onOpen,
}: {
  row: Row
  showPath?: boolean
  tone: 'include' | 'exclude'
  picked: boolean
  onToggle: () => void
  onOpen: () => void
}) {
  const { t } = useLingui()
  const { label } = row
  const canOpen = row.children > 0
  const toggleLabel = picked ? t`Remove ${label}` : tone === 'include' ? t`Include ${label}` : t`Exclude ${label}`
  const subtags = row.children

  return (
    <div className="tag-browser-row" data-state={picked ? tone : undefined}>
      {row.addable ? (
        <Checkbox
          size="sm"
          color={tone === 'include' ? 'var(--ok)' : 'var(--danger-fill)'}
          iconColor={tone === 'include' ? 'var(--ok-on)' : undefined}
          checked={picked}
          onChange={onToggle}
          aria-label={toggleLabel}
        />
      ) : (
        <span className="tag-browser-spacer" />
      )}
      <UnstyledButton
        className="tag-browser-name"
        onClick={canOpen ? onOpen : onToggle}
        disabled={!canOpen && !row.addable}
        aria-label={canOpen ? t`Open ${label}` : toggleLabel}
      >
        <div style={{ minWidth: 0, flex: 1 }}>
          <Text size="sm" truncate fw={row.addable ? 400 : 600} td={picked && tone === 'exclude' ? 'line-through' : undefined}>
            {label}
          </Text>
          {showPath && row.path && (
            <Text size="xs" c="var(--ink-3)" truncate>
              {row.path}
            </Text>
          )}
        </div>
        <Group gap="sm" wrap="nowrap" style={{ flexShrink: 0 }}>
          {canOpen && row.kind === 'tag' && (
            <Text size="xs" c="var(--ink-4)">
              <Plural value={subtags} one="# subtag" other="# subtags" />
            </Text>
          )}
          {row.count != null && (
            <Text size="xs" c="var(--ink-3)" fw={500}>
              {row.count.toLocaleString()}
            </Text>
          )}
        </Group>
      </UnstyledButton>
      {canOpen ? (
        <ActionIcon variant="subtle" color="var(--neutral)" onClick={onOpen} aria-label={t`Open ${label}`}>
          <IconChevronRight size={16} />
        </ActionIcon>
      ) : (
        <span className="tag-browser-spacer" />
      )}
    </div>
  )
}
