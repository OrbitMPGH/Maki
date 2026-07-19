import { Group, Modal, ScrollArea, Stack, Text, TextInput } from '@mantine/core'
import { useDisclosure, useHotkeys } from '@mantine/hooks'
import { IconBooks, IconSearch } from '@tabler/icons-react'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { useSeries } from '../api/hooks'
import type { NavItem } from '../nav'

interface Props {
  navItems: NavItem[]
}

type Result =
  | { kind: 'nav'; key: string; label: string; sub: string; icon: NavItem['icon']; path: string }
  | { kind: 'series'; key: string; label: string; sub: string; coverUrl: string | null; path: string }

const MAX_SERIES_RESULTS = 8

export default function CommandPalette({ navItems }: Props) {
  const [opened, { open, close }] = useDisclosure(false)
  const [query, setQuery] = useState('')
  const [selected, setSelected] = useState(0)
  const navigate = useNavigate()
  const { data: series } = useSeries()
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

    return [...navMatches, ...seriesMatches]
  }, [query, navItems, series])

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

      <Modal
        opened={opened}
        onClose={close}
        withCloseButton={false}
        padding={0}
        radius="md"
        size="lg"
        centered
        transitionProps={{ transition: 'pop', duration: 120 }}
      >
        <Stack gap={0}>
          <TextInput
            autoFocus
            value={query}
            onChange={(e) => setQuery(e.currentTarget.value)}
            onKeyDown={onKeyDown}
            placeholder="Jump to a series or page…"
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
