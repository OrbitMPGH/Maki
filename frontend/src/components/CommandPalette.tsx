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
    const navMatches = navItems
      .filter((item) => !q || item.label.toLowerCase().includes(q))
      .map((item) => ({
        kind: 'nav' as const,
        key: `nav-${item.path}`,
        label: item.label,
        sub: 'Page',
        icon: item.icon,
        path: item.path,
      }))

    // Individual settings, not just the Settings page: a card is only reachable now if you know
    // which tab it sits under, and searching is the answer to that. Filtered by what the caller may
    // actually see, so a non-admin is never sent to a tab that doesn't exist for them.
    const settingMatches = q
      ? SETTINGS_ENTRIES.filter(
          (e) => entryVisible(e, isAdmin, can) && matchesSettingsQuery(e, q),
        ).map((e) => ({
          kind: 'setting' as const,
          key: `setting-${e.id}`,
          label: e.title,
          sub: `Settings › ${SETTINGS_TABS.find((t) => t.key === e.tab)?.label ?? ''}`,
          path: settingsPath(e),
        }))
      : []

    const seriesMatches = q
      ? (series ?? [])
          .filter(
            (s) =>
              s.title.toLowerCase().includes(q) ||
              s.sortTitle.toLowerCase().includes(q) ||
              s.originalTitle?.toLowerCase().includes(q),
          )
          .slice(0, MAX_SERIES_RESULTS)
          .map((s) => ({
            kind: 'series' as const,
            key: `series-${s.id}`,
            label: s.title,
            sub: s.status,
            coverUrl: s.coverUrl,
            path: `/series/${s.id}`,
          }))
      : []

    // Last, always: the palette only searches the local library, so a title that isn't in it yet
    // has no result at all. This hands the same typed text to /add, which searches MangaBaka:
    // "add" or "request" depending on what the caller may do, matching the page's own verb.
    const searchFallback: Result[] = q
      ? [
          {
            kind: 'search' as const,
            key: 'search-metadata',
            label: `Search for “${query.trim()}”`,
            sub: canAdd ? 'Add series' : 'Request series',
            path: `/add?q=${encodeURIComponent(query.trim())}`,
          },
        ]
      : []

    return [...navMatches, ...settingMatches, ...seriesMatches, ...searchFallback]
  }, [query, navItems, series, isAdmin, can, canAdd])

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
        aria-label="Search (Ctrl+K)"
      >
        <IconSearch size={16} stroke={1.8} />
        <span className="command-palette-trigger-label">Search…</span>
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
            placeholder="Jump to a series, page or setting…"
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
                <Text c="dimmed" size="sm" ta="center" py="lg">
                  No matches.
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
                    <Text size="xs" c="dimmed" truncate>
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
