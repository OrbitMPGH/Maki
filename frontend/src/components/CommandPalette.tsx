import { Center, Group, Loader, Modal, ScrollArea, Stack, Text, TextInput } from '@mantine/core'
import { useDisclosure, useHotkeys, useOs } from '@mantine/hooks'
import { IconAdjustments, IconArchive, IconBell, IconBooks, IconPlus, IconSearch, IconSend } from '../lib/icons'
import { useCallback, useEffect, useId, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { fuzzyFilter, titleIndex } from '../lib/fuzzySearch'
import { DUR, EASE_CSS, pacedMs } from '../lib/motion'
import { useNavigate } from 'react-router-dom'
import { useSeries } from '../api/hooks'
import { useAuth } from '../auth/AuthProvider'
import { SETTINGS_TABS, matchesSettingsQuery, settingsPath } from '../pages/settings/registry'
import { useVisibleSettingsEntries } from '../pages/settings/useVisibleSettingsEntries'
import { useLingui } from '@lingui/react'
import { Trans, useLingui as useLinguiMacro } from '@lingui/react/macro'
import { msg } from '@lingui/core/macro'
import { useLabel } from '../i18n-context'
import { seriesStatusVisual } from './ui/status'
import type { SettingsTabKey } from '../pages/settings/registry'
import type { NavItem } from '../nav'

const LIST_MAX_HEIGHT = 360

interface Props {
  navItems: NavItem[]
}

interface ModalProps extends Props {
  opened: boolean
  onOpen: () => void
  onClose: () => void
}

type Result =
  | { kind: 'nav'; key: string; label: string; sub: string; icon: NavItem['icon']; path: string }
  | { kind: 'setting'; key: string; label: string; sub: string; path: string }
  | {
      kind: 'series'
      key: string
      label: string
      sub: string
      coverUrl: string | null
      path: string
      /** The other title the query matched, when the shown one has no match to mark. */
      alt: string | null
    }
  | { kind: 'search'; key: string; label: string; sub: string; path: string }

const MAX_SERIES_RESULTS = 8

/**
 * Wraps the parts of `label` that match a term in <mark>. The plain parts stay bare text nodes
 * rather than spans (Mantine's Highlight wraps them), because the accessible name drops the space at
 * the edge of a span and the row would be announced as "PaperMoon Detective".
 */
function markMatches(label: string, terms: string[]): ReactNode {
  if (terms.length === 0) return label
  const escaped = [...terms]
    .sort((a, b) => b.length - a.length)
    .map((term) => term.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'))
  const parts = label.split(new RegExp(`(${escaped.join('|')})`, 'gi'))
  return parts.map((part, i) => (i % 2 === 1 ? <mark key={i}>{part}</mark> : part))
}

function containsTerm(text: string, terms: string[]) {
  const lower = text.toLowerCase()
  return terms.some((term) => lower.includes(term.toLowerCase()))
}

/** The first other title holding the whole query, else any of its words; null when the shown one already has a match. */
function matchingAltTitle(label: string, others: (string | null | undefined)[], terms: string[]): string | null {
  if (terms.length === 0 || containsTerm(label, terms)) return null
  const candidates = others.filter((o): o is string => !!o && o !== label)
  return (
    candidates.find((o) => containsTerm(o, terms.slice(0, 1))) ??
    candidates.find((o) => containsTerm(o, terms)) ??
    null
  )
}

/** The search pill. `icon` is the bare circle the phone's top bar has room for. */
export function CommandPaletteTrigger({
  onOpen,
  variant = 'bar',
}: {
  onOpen: () => void
  variant?: 'bar' | 'icon'
}) {
  const isMac = useOs() === 'macos'
  return (
    <button
      type="button"
      className={
        variant === 'icon'
          ? 'command-palette-trigger command-palette-trigger-icon'
          : 'command-palette-trigger'
      }
      onClick={onOpen}
      aria-keyshortcuts={isMac ? 'Meta+K' : 'Control+K'}
    >
      <IconSearch size={16} />
      <span className="command-palette-trigger-label">
        <Trans>Search</Trans>
      </span>
      {/* The key names themselves, not words: the same two keys whatever the reader speaks. */}
      <span className="command-palette-trigger-kbd">{isMac ? '⌘ K' : 'Ctrl K'}</span>
    </button>
  )
}

export default function CommandPalette({ navItems }: Props) {
  const [opened, { open, close }] = useDisclosure(false)
  return (
    <>
      <CommandPaletteTrigger onOpen={open} />
      <CommandPaletteModal navItems={navItems} opened={opened} onOpen={open} onClose={close} />
    </>
  )
}

export function CommandPaletteModal({ navItems, opened, onOpen: open, onClose: close }: ModalProps) {
  const [query, setQuery] = useState('')
  const [selected, setSelected] = useState(0)
  const navigate = useNavigate()
  // The full library list is only needed once the palette is open; the cache still serves it
  // from other pages while closed.
  const { data: series, isFetching: seriesLoading } = useSeries(opened)
  const { can } = useAuth()
  const { _, i18n } = useLingui()
  const { t } = useLinguiMacro()
  const renderLabel = useLabel()
  const canAdd = can('AddSeries')
  const listRef = useRef<HTMLDivElement>(null)
  const keyboardMoved = useRef(false)
  const listId = useId()
  const optionId = (i: number) => `${listId}-option-${i}`
  const visibleSettings = useVisibleSettingsEntries()

  // No ignored tags: mod+K has no editing meaning, and people reach for it while typing in a field.
  useHotkeys([['mod+K', open]], [])

  useEffect(() => {
    if (!opened) {
      setQuery('')
      setSelected(0)
    }
  }, [opened])

  // Typing the displayed name has to find the series even when a language preference has moved it
  // off the canonical title, and typing the canonical name still has to.
  const seriesTitles = useMemo(
    () =>
      titleIndex(series ?? [], (s) => [
        s.title, s.displayTitle, s.sortTitle, s.originalTitle, ...(s.altTitleNames ?? []),
      ]),
    [series],
  )

  // The whole query first, then its words, since a fuzzy or multi-word match rarely contains the
  // query as typed. A typo match highlights nothing, which is honest about what matched.
  const highlightTerms = useMemo(() => {
    const typed = query.trim()
    if (!typed) return []
    return [typed, ...typed.split(/\s+/).filter((w) => w.length > 1)]
  }, [query])

  const results = useMemo<Result[]>(() => {
    const q = query.trim().toLowerCase()

    // The tab name is itself a descriptor, so the breadcrumb is built from two renders rather than
    // from a plain string. Kept as one message so the separator can move where a language needs it.
    const settingsCrumb = (tab: SettingsTabKey) => {
      const label = SETTINGS_TABS.find((candidate) => candidate.key === tab)?.label
      const tabName = label ? _(label) : ''
      return _(msg`Settings › ${tabName}`)
    }
    // Rendered here rather than taken from the table, and the memo depends on the active locale
    // below, because the nav labels are descriptors. Matching against them unrendered would search
    // English while the user reads their own language, which looks like search quietly breaking.
    const navMatches = navItems
      .map((item) => ({ item, label: _(item.label) }))
      .filter(({ label }) => !q || label.toLowerCase().includes(q))
      .map(({ item, label }) => ({
        kind: 'nav' as const,
        key: `nav-${item.path}`,
        label,
        sub: _(msg`Page`),
        icon: item.icon,
        path: item.path,
      }))

    // Not a sidebar page, so it only turns up when searched for, and only for whoever has the library.
    const archivedLabel = _(msg({ message: 'Archived', context: 'series' }))
    const archivedNav =
      q && navItems.some((item) => item.path === '/library') && archivedLabel.toLowerCase().includes(q)
        ? [
            {
              kind: 'nav' as const,
              key: 'nav-/library/archived',
              label: archivedLabel,
              sub: _(msg`Page`),
              icon: IconArchive,
              path: '/library/archived',
            },
          ]
        : []

    // Reached from the bell rather than the sidebar, and open to every signed-in user.
    const notificationsLabel = _(msg`Notifications`)
    const notificationsNav =
      !q || notificationsLabel.toLowerCase().includes(q)
        ? [
            {
              kind: 'nav' as const,
              key: 'nav-/notifications',
              label: notificationsLabel,
              sub: _(msg`Page`),
              icon: IconBell,
              path: '/notifications',
            },
          ]
        : []

    // Individual settings, not just the Settings page: a card is only reachable now if you know
    // which tab it sits under, and searching is the answer to that. Filtered by what the caller may
    // actually see, so a non-admin is never sent to a tab that doesn't exist for them.
    const settingMatches = q
      ? visibleSettings
          .filter((e) => matchesSettingsQuery(e, q, _))
          .map((e) => ({
            kind: 'setting' as const,
            key: `setting-${e.id}`,
            label: _(e.title),
            sub: settingsCrumb(e.tab),
            path: settingsPath(e),
          }))
      : []

    const seriesMatches = q
      ? fuzzyFilter(series ?? [], q, seriesTitles)
          .slice(0, MAX_SERIES_RESULTS)
          .map((s) => ({
            kind: 'series' as const,
            key: `series-${s.id}`,
            label: s.displayTitle,
            sub: s.archivedAt != null ? archivedLabel : renderLabel(seriesStatusVisual(s.status).label),
            coverUrl: s.coverUrl,
            path: `/series/${s.id}`,
            alt: matchingAltTitle(
              s.displayTitle,
              [s.title, s.originalTitle, ...(s.altTitles ?? []).map((a) => a.title)],
              highlightTerms,
            ),
          }))
      : []

    // Last, always: the palette only searches the local library, so a title that isn't in it yet
    // has no result at all. This hands the same typed text to /add, which searches MangaBaka:
    // "add" or "request" depending on what the caller may do, matching the page's own verb.
    const typed = query.trim()
    const searchFallback: Result[] = q
      ? [
          {
            kind: 'search' as const,
            key: 'search-metadata',
            label: _(msg`Search for “${typed}”`),
            sub: canAdd ? _(msg`Add series`) : _(msg`Request series`),
            path: `/add?q=${encodeURIComponent(query.trim())}`,
          },
        ]
      : []

    return [...navMatches, ...notificationsNav, ...archivedNav, ...settingMatches, ...seriesMatches, ...searchFallback]
  }, [query, navItems, series, seriesTitles, visibleSettings, canAdd, renderLabel, highlightTerms, _, i18n.locale])

  useEffect(() => {
    setSelected(0)
  }, [results.length])

  // The list stops at the last row that fits whole under the cap, so the panel never ends on half
  // a row. Rows differ in height (an alternate title adds a line), so it is measured, not fixed.
  const rowsRef = useRef<HTMLDivElement | null>(null)
  const [listMax, setListMax] = useState<number>()
  const fitList = useCallback(() => {
    const stack = rowsRef.current
    if (!stack) return
    const cap = Math.min(LIST_MAX_HEIGHT, window.innerHeight * 0.6)
    const pad = parseFloat(getComputedStyle(stack).paddingBottom) || 0
    let fit = cap
    for (const row of Array.from(stack.children) as HTMLElement[]) {
      const top = row.offsetParent === stack ? row.offsetTop : row.offsetTop - stack.offsetTop
      const bottom = top + row.offsetHeight + pad
      if (bottom > cap) break
      fit = bottom
    }
    setListMax(Math.round(fit))
  }, [])
  const rowsRefCallback = useCallback(
    (el: HTMLDivElement | null) => {
      rowsRef.current = el
      fitList()
    },
    [fitList],
  )
  useLayoutEffect(fitList, [fitList, results])

  // Only a keyboard move scrolls: a mouse hover is already on a visible row, and scrolling under
  // the pointer would make the list chase it.
  useEffect(() => {
    if (!keyboardMoved.current) return
    keyboardMoved.current = false
    listRef.current
      ?.querySelector('[data-active="true"]')
      ?.scrollIntoView({ block: 'nearest' })
  }, [selected])

  function go(result: Result) {
    navigate(result.path)
    close()
  }

  function onKeyDown(e: React.KeyboardEvent) {
    if (e.key === 'ArrowDown') {
      e.preventDefault()
      keyboardMoved.current = true
      setSelected((i) => (results.length ? (i + 1) % results.length : 0))
    } else if (e.key === 'ArrowUp') {
      e.preventDefault()
      keyboardMoved.current = true
      setSelected((i) => (results.length ? (i - 1 + results.length) % results.length : 0))
    } else if (e.key === 'Enter') {
      e.preventDefault()
      const pick = results[selected]
      if (pick) go(pick)
    }
  }

  return (
    <>
      {/* Explicit zIndex: opened globally via the mod+K hotkey, which stays live even while
          another modal is open, so this must render above the highest zIndex any other modal
          in the app uses (DiscoverDetailModal's 1000/1001). */}
      <Modal
        opened={opened}
        onClose={close}
        withCloseButton={false}
        padding={0}
        radius="md"
        size="lg"
        centered
        transitionProps={{ transition: 'pop', duration: pacedMs(DUR.fast), timingFunction: EASE_CSS }}
        zIndex={1100}
        attributes={{ content: { 'aria-label': t`Search` } }}
      >
        <Stack gap={0}>
          <TextInput
            aria-label={t`Jump to a series, page or setting…`}
            autoFocus
            value={query}
            onChange={(e) => setQuery(e.currentTarget.value)}
            onKeyDown={onKeyDown}
            placeholder={t`Jump to a series, page or setting…`}
            role="combobox"
            aria-expanded
            aria-controls={listId}
            aria-autocomplete="list"
            aria-activedescendant={results.length > 0 ? optionId(selected) : undefined}
            leftSection={<IconSearch size={16} />}
            variant="unstyled"
            size="lg"
            px="md"
            py={4}
            style={{ borderBottom: '1px solid var(--border)' }}
          />
          <ScrollArea.Autosize
            mah={listMax ?? `min(${LIST_MAX_HEIGHT}px, 60dvh)`}
            type="auto"
            scrollbars="y"
            viewportRef={listRef}
            className="command-palette-list"
          >
            <Stack ref={rowsRefCallback} gap={2} p="xs" id={listId} role={results.length > 0 ? 'listbox' : undefined}>
              {results.length === 0 && (
                // On a cold cache the library is still loading, so an empty list is not an answer yet.
                seriesLoading && !series ? (
                  <Center py="lg">
                    <Loader size="sm" />
                  </Center>
                ) : (
                  <Text c="var(--ink-3)" size="sm" ta="center" py="lg">
                    <Trans>No matches.</Trans>
                  </Text>
                )
              )}
              {results.map((r, i) => (
                <Group
                  key={r.key}
                  id={optionId(i)}
                  role="option"
                  aria-selected={i === selected}
                  gap="sm"
                  wrap="nowrap"
                  px="sm"
                  py={8}
                  className="command-palette-item"
                  data-active={i === selected}
                  onMouseEnter={() => setSelected(i)}
                  onClick={() => go(r)}
                  style={{ cursor: 'pointer', borderRadius: 'var(--radius-thumb)' }}
                >
                  {r.kind === 'nav' ? (
                    <r.icon size={18} />
                  ) : r.kind === 'setting' ? (
                    <IconAdjustments size={18} />
                  ) : r.kind === 'search' ? (
                    canAdd ? (
                      <IconPlus size={18} />
                    ) : (
                      <IconSend size={18} />
                    )
                  ) : r.coverUrl ? (
                    <img
                      src={r.coverUrl}
                      alt=""
                      width={24}
                      height={32}
                      style={{ objectFit: 'cover', borderRadius: 'var(--mantine-radius-xs)', flexShrink: 0 }}
                    />
                  ) : (
                    <IconBooks size={18} />
                  )}
                  <Stack gap={0} style={{ minWidth: 0 }}>
                    <Text size="sm" fw={600} truncate>
                      {/* The fallback's label is the query itself, so marking it would mark all of it. */}
                      {r.kind === 'search' ? r.label : markMatches(r.label, highlightTerms)}
                    </Text>
                    {r.kind === 'series' && r.alt && (
                      <Text size="xs" c="var(--ink-3)" truncate>
                        {markMatches(r.alt, highlightTerms)}
                      </Text>
                    )}
                    <Text size="xs" c="var(--ink-3)" truncate>
                      {r.sub}
                    </Text>
                  </Stack>
                </Group>
              ))}
            </Stack>
          </ScrollArea.Autosize>
        </Stack>
      </Modal>
    </>
  )
}
