import { Group, Modal, ScrollArea, Stack, Text, TextInput } from '@mantine/core'
import { useDisclosure, useHotkeys } from '@mantine/hooks'
import { IconAdjustments, IconBooks, IconPlus, IconSearch, IconSend } from '@tabler/icons-react'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { useSeries } from '../api/hooks'
import { useAuth } from '../auth/AuthProvider'
import {
  SETTINGS_ENTRIES,
  SETTINGS_TABS,
  entryVisible,
  matchesSettingsQuery,
  settingsPath,
} from '../pages/settings/registry'
import { useLingui } from '@lingui/react'
import { Trans, useLingui as useLinguiMacro } from '@lingui/react/macro'
import { msg } from '@lingui/core/macro'
import { useLabel } from '../i18n-context'
import { seriesStatusVisual } from './ui/status'
import type { SettingsTabKey } from '../pages/settings/registry'
import type { NavItem } from '../nav'

interface Props {
  navItems: NavItem[]
}

type Result =
  | { kind: 'nav'; key: string; label: string; sub: string; icon: NavItem['icon']; path: string }
  | { kind: 'setting'; key: string; label: string; sub: string; path: string }
  | { kind: 'series'; key: string; label: string; sub: string; coverUrl: string | null; path: string }
  | { kind: 'search'; key: string; label: string; sub: string; path: string }

const MAX_SERIES_RESULTS = 8

export default function CommandPalette({ navItems }: Props) {
  const [opened, { open, close }] = useDisclosure(false)
  const [query, setQuery] = useState('')
  const [selected, setSelected] = useState(0)
  const navigate = useNavigate()
  const { data: series } = useSeries()
  const { me, can } = useAuth()
  const { _, i18n } = useLingui()
  const { t } = useLinguiMacro()
  const renderLabel = useLabel()
  const isAdmin = me?.isAdmin ?? false
  const canAdd = can('AddSeries')
  const listRef = useRef<HTMLDivElement>(null)

  useHotkeys([['mod+K', open]])

  useEffect(() => {
    if (!opened) {
      setQuery('')
      setSelected(0)
    }
  }, [opened])

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

    // Individual settings, not just the Settings page: a card is only reachable now if you know
    // which tab it sits under, and searching is the answer to that. Filtered by what the caller may
    // actually see, so a non-admin is never sent to a tab that doesn't exist for them.
    const settingMatches = q
      ? SETTINGS_ENTRIES.filter(
          (e) => entryVisible(e, isAdmin, can) && matchesSettingsQuery(e, q, _),
        ).map((e) => ({
          kind: 'setting' as const,
          key: `setting-${e.id}`,
          label: _(e.title),
          sub: settingsCrumb(e.tab),
          path: settingsPath(e),
        }))
      : []

    const seriesMatches = q
      ? (series ?? [])
          .filter(
            (s) =>
              s.title.toLowerCase().includes(q) ||
              // Typing the displayed name has to find the series even when a language preference
              // has moved it off the canonical title, and typing the canonical name still has to.
              s.displayTitle.toLowerCase().includes(q) ||
              s.sortTitle.toLowerCase().includes(q) ||
              s.originalTitle?.toLowerCase().includes(q) ||
              s.altTitles.some((t) => t.title.toLowerCase().includes(q)),
          )
          .slice(0, MAX_SERIES_RESULTS)
          .map((s) => ({
            kind: 'series' as const,
            key: `series-${s.id}`,
            label: s.displayTitle,
            sub: renderLabel(seriesStatusVisual(s.status).label),
            coverUrl: s.coverUrl,
            path: `/series/${s.id}`,
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

    return [...navMatches, ...settingMatches, ...seriesMatches, ...searchFallback]
  }, [query, navItems, series, isAdmin, can, canAdd, renderLabel, _, i18n.locale])

  useEffect(() => {
    setSelected(0)
  }, [results.length])

  function go(result: Result) {
    navigate(result.path)
    close()
  }

  function onKeyDown(e: React.KeyboardEvent) {
    if (e.key === 'ArrowDown') {
      e.preventDefault()
      setSelected((i) => (results.length ? (i + 1) % results.length : 0))
    } else if (e.key === 'ArrowUp') {
      e.preventDefault()
      setSelected((i) => (results.length ? (i - 1 + results.length) % results.length : 0))
    } else if (e.key === 'Enter') {
      e.preventDefault()
      const pick = results[selected]
      if (pick) go(pick)
    }
  }

  return (
    <>
      <button
        type="button"
        className="command-palette-trigger"
        onClick={open}
        aria-label={t`Search (Ctrl+K)`}
      >
        <IconSearch size={16} stroke={1.8} />
        <span className="command-palette-trigger-label">
          <Trans>Search…</Trans>
        </span>
        {/* The key names themselves, not words: the same two keys whatever the reader speaks. */}
        <span className="command-palette-trigger-kbd">Ctrl K</span>
      </button>

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
        transitionProps={{ transition: 'pop', duration: 120 }}
        zIndex={1100}
      >
        <Stack gap={0}>
          <TextInput
            autoFocus
            value={query}
            onChange={(e) => setQuery(e.currentTarget.value)}
            onKeyDown={onKeyDown}
            placeholder={t`Jump to a series, page or setting…`}
            leftSection={<IconSearch size={16} />}
            variant="unstyled"
            size="lg"
            px="md"
            py={4}
            style={{ borderBottom: '1px solid var(--border)' }}
          />
          <ScrollArea.Autosize mah={360} type="auto" viewportRef={listRef}>
            <Stack gap={2} p="xs">
              {results.length === 0 && (
                <Text c="var(--ink-3)" size="sm" ta="center" py="lg">
                  <Trans>No matches.</Trans>
                </Text>
              )}
              {results.map((r, i) => (
                <Group
                  key={r.key}
                  gap="sm"
                  wrap="nowrap"
                  px="sm"
                  py={8}
                  className="command-palette-item"
                  data-active={i === selected}
                  onMouseEnter={() => setSelected(i)}
                  onClick={() => go(r)}
                  style={{ cursor: 'pointer', borderRadius: 8 }}
                >
                  {r.kind === 'nav' ? (
                    <r.icon size={18} stroke={1.7} />
                  ) : r.kind === 'setting' ? (
                    <IconAdjustments size={18} stroke={1.7} />
                  ) : r.kind === 'search' ? (
                    canAdd ? (
                      <IconPlus size={18} stroke={1.7} />
                    ) : (
                      <IconSend size={18} stroke={1.7} />
                    )
                  ) : r.coverUrl ? (
                    <img
                      src={r.coverUrl}
                      alt=""
                      width={24}
                      height={32}
                      style={{ objectFit: 'cover', borderRadius: 3, flexShrink: 0 }}
                    />
                  ) : (
                    <IconBooks size={18} stroke={1.7} />
                  )}
                  <Stack gap={0} style={{ minWidth: 0 }}>
                    <Text size="sm" fw={550} truncate>
                      {r.label}
                    </Text>
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
