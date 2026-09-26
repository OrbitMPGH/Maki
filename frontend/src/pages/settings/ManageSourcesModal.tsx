import { useEffect, useMemo, useRef, useState, type DragEvent, type ReactNode } from 'react'
import {
  ActionIcon, Button, Chip, Group, Kbd, Modal, Popover, SegmentedControl, Switch, Tabs, Text,
  TextInput,
} from '@mantine/core'
import { useHotkeys, useMediaQuery } from '@mantine/hooks'
import { notifications } from '@mantine/notifications'
import {
  IconArrowUp, IconChevronDown, IconChevronUp, IconFilter, IconGripVertical, IconSearch,
  IconShield,
} from '@tabler/icons-react'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { plural, t as now } from '@lingui/core/macro'
import {
  useSaveSourcePriority, useSourcePriority, useSources,
  type SourceContent, type SourceInfo,
} from '../../api/hooks'
import { languageName } from '../../api/titles'
import { SourceIcon, baseLanguage, sourceHost } from '../../sourceIcons'
import { ConfirmDialog } from '../../components/ui/ConfirmDialog'

type StateFilter = 'all' | 'on' | 'off'
type Pane = 'catalogue' | 'priority'

const CONTENT_ORDER: SourceContent[] = ['manga', 'manhwa', 'manhua', 'webtoon', 'doujinshi']

/** Language names in the reader's UI language, falling back to the English table in `titles.ts`. */
function useLanguageNamer() {
  const { i18n } = useLingui()
  return useMemo(() => {
    let names: Intl.DisplayNames | null = null
    try {
      names = new Intl.DisplayNames([i18n.locale], { type: 'language' })
    } catch {
      names = null
    }
    return (code: string) => {
      try {
        return names?.of(code) ?? languageName(code) ?? code
      } catch {
        return languageName(code) ?? code
      }
    }
  }, [i18n.locale])
}

function useContentLabels(): Record<SourceContent, string> {
  const { t } = useLingui()
  return {
    manga: t`Manga`,
    manhwa: t`Manhwa`,
    manhua: t`Manhua`,
    webtoon: t`Webtoon`,
    doujinshi: t`Doujinshi`,
  }
}

function SourceTags({ source, contentLabels }: {
  source: SourceInfo
  contentLabels: Record<SourceContent, string>
}) {
  const languages = source.supportedLanguages ?? []
  const languageCount = new Set(languages.map(baseLanguage)).size
  return (
    <div className="source-tags">
      {source.kind === 'official' && <span className="source-tag" data-tone="official"><Trans>Official</Trans></span>}
      {source.kind === 'scanlator' && <span className="source-tag" data-tone="kind"><Trans>Scanlator</Trans></span>}
      {source.kind === 'aggregator' && <span className="source-tag" data-tone="kind"><Trans>Aggregator</Trans></span>}
      {(source.content ?? []).map((c) => contentLabels[c] && (
        <span key={c} className="source-tag">{contentLabels[c]}</span>
      ))}
      {source.rating === 'adult' && <span className="source-tag" data-tone="adult"><Trans>18+</Trans></span>}
      {source.rating === 'mature' && <span className="source-tag" data-tone="mature"><Trans>Mature</Trans></span>}
      {languageCount > 3 ? (
        <span className="source-tag" data-tone="languages">
          <Plural value={languageCount} one="# language" other="# languages" />
        </span>
      ) : (
        languages.map((code) => (
          <span key={code} className="source-tag" data-tone="language">{code}</span>
        ))
      )}
      {source.needsFlareSolverr && (
        <span className="source-tag" data-tone="flare">
          <IconShield size={11} stroke={2.4} />
          <Trans>FlareSolverr</Trans>
        </span>
      )}
      {source.supportsLanguageFilter && (
        <span className="source-tag" data-tone="filter"><Trans>Language filter</Trans></span>
      )}
    </div>
  )
}

/**
 * The enabled sources in rank order, reordered by dragging or the arrow buttons. Dragging follows
 * PriorityList: rows only shift visually while the pointer moves and the order commits on drop,
 * because reordering the DOM mid-drag slides rows past a still cursor and re-triggers.
 */
function PriorityRail({ items, onChange, labelOf }: {
  items: string[]
  onChange: (order: string[]) => void
  labelOf: (name: string) => string
}) {
  const { t } = useLingui()
  const [dragFrom, setDragFrom] = useState<number | null>(null)
  const [hover, setHover] = useState<number | null>(null)
  const [rowHeight, setRowHeight] = useState(0)
  const listRef = useRef<HTMLDivElement>(null)

  function handleDragOver(e: DragEvent) {
    e.preventDefault()
    e.dataTransfer.dropEffect = 'move'
    if (dragFrom === null || !listRef.current || rowHeight === 0) return
    const rect = listRef.current.getBoundingClientRect()
    const index = Math.floor((e.clientY - rect.top) / rowHeight)
    setHover(Math.min(Math.max(index, 0), items.length - 1))
  }

  function commit(e: DragEvent) {
    if (e.dataTransfer.dropEffect !== 'none' && dragFrom !== null && hover !== null && dragFrom !== hover) {
      const next = [...items]
      const [moved] = next.splice(dragFrom, 1)
      next.splice(hover, 0, moved)
      onChange(next)
    }
    setDragFrom(null)
    setHover(null)
  }

  function move(index: number, by: number) {
    const to = index + by
    if (to < 0 || to >= items.length) return
    const next = [...items]
    ;[next[index], next[to]] = [next[to], next[index]]
    onChange(next)
  }

  if (items.length === 0) {
    return (
      <div className="source-manager-empty">
        <Trans>Nothing enabled. Turn a source on to rank it.</Trans>
      </div>
    )
  }

  return (
    <div ref={listRef} onDragOver={handleDragOver} onDrop={(e) => e.preventDefault()}>
      {items.map((name, i) => {
        let shift = 0
        if (dragFrom !== null && hover !== null && i !== dragFrom) {
          if (dragFrom < hover && i > dragFrom && i <= hover) shift = -1
          else if (dragFrom > hover && i >= hover && i < dragFrom) shift = 1
        }
        const label = labelOf(name)
        return (
          <div
            key={name}
            className="source-rail-row"
            draggable
            data-dragging={dragFrom === i || undefined}
            onDragStart={(e) => {
              const original = e.currentTarget
              const clone = original.cloneNode(true) as HTMLElement
              clone.style.position = 'fixed'
              clone.style.top = '-9999px'
              clone.style.left = '-9999px'
              clone.style.width = `${original.offsetWidth}px`
              document.body.appendChild(clone)
              e.dataTransfer.effectAllowed = 'move'
              e.dataTransfer.setDragImage(clone, e.nativeEvent.offsetX, e.nativeEvent.offsetY)
              setTimeout(() => document.body.removeChild(clone), 0)
              setDragFrom(i)
              setHover(i)
              setRowHeight(original.getBoundingClientRect().height)
            }}
            onDragEnd={commit}
            style={{
              transform: shift ? `translateY(${shift * rowHeight}px)` : undefined,
              pointerEvents: dragFrom !== null && i !== dragFrom ? 'none' : undefined,
            }}
          >
            <IconGripVertical size={14} className="source-rail-grip" />
            <span className="source-rail-rank">{i + 1}</span>
            <SourceIcon name={name} label={label} size={28} />
            <span className="source-rail-name">{label}</span>
            <span className="source-rail-moves">
              <ActionIcon
                variant="subtle" color="gray" size="sm" disabled={i === 0}
                aria-label={t`Move ${label} up`}
                onClick={() => move(i, -1)}
              >
                <IconChevronUp size={14} />
              </ActionIcon>
              <ActionIcon
                variant="subtle" color="gray" size="sm" disabled={i === items.length - 1}
                aria-label={t`Move ${label} down`}
                onClick={() => move(i, 1)}
              >
                <IconChevronDown size={14} />
              </ActionIcon>
            </span>
          </div>
        )
      })}
    </div>
  )
}

/**
 * Every registered source: switch them on or off in the catalogue and rank the enabled ones on the
 * rail. Edits stay local until Save, which writes the whole thing through the priority endpoint
 * once. Disabled sources are appended after the ranked ones, so a source switched back on later
 * joins at the bottom, the same as it does here.
 */
export function ManageSourcesModal({ opened, onClose }: { opened: boolean; onClose: () => void }) {
  const { t } = useLingui()
  const narrow = useMediaQuery('(max-width: 760px)') ?? false
  const { data: sources, isFetching: sourcesFetching } = useSources()
  const { data: priority, isFetching: priorityFetching } = useSourcePriority(opened)
  const save = useSaveSourcePriority()
  const contentLabels = useContentLabels()
  const nameOfLanguage = useLanguageNamer()

  const [rail, setRail] = useState<string[]>([])
  const [initialRail, setInitialRail] = useState<string[]>([])
  const [pane, setPane] = useState<Pane>('catalogue')
  const [search, setSearch] = useState('')
  const [stateFilter, setStateFilter] = useState<StateFilter>('all')
  const [contentFilter, setContentFilter] = useState<SourceContent[]>([])
  const [adultOnly, setAdultOnly] = useState(false)
  const [languageFilter, setLanguageFilter] = useState<string | null>(null)
  const [filtersOpen, setFiltersOpen] = useState(false)
  const [confirmDiscard, setConfirmDiscard] = useState(false)
  const searchRef = useRef<HTMLInputElement>(null)

  // Seed once per opening. A background refetch of the priority must not wipe a draft in progress.
  const seeded = useRef(false)
  useEffect(() => {
    if (!opened) {
      seeded.current = false
      return
    }
    // Waiting out a refetch keeps a reopen straight after a save from seeding the pre-save order.
    if (seeded.current || !priority || priorityFetching || sourcesFetching) return
    seeded.current = true
    const start = priority.order.filter((name) => !priority.disabled.includes(name))
    setRail(start)
    setInitialRail(start)
    setPane('catalogue')
    setSearch('')
    setStateFilter('all')
    setContentFilter([])
    setAdultOnly(false)
    setLanguageFilter(null)
  }, [opened, priority, priorityFetching, sourcesFetching])

  useHotkeys(opened ? [['/', () => searchRef.current?.focus()]] : [])

  const byName = useMemo(() => new Map((sources ?? []).map((s) => [s.name, s])), [sources])
  const labelOf = (name: string) => byName.get(name)?.displayName ?? name
  const railSet = useMemo(() => new Set(rail), [rail])

  const languageCounts = useMemo(() => {
    const counts = new Map<string, number>()
    for (const source of sources ?? []) {
      for (const code of new Set((source.supportedLanguages ?? []).map(baseLanguage))) {
        counts.set(code, (counts.get(code) ?? 0) + 1)
      }
    }
    return [...counts.entries()].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]))
  }, [sources])

  const needle = search.trim().toLowerCase()
  const visible = (sources ?? []).filter((source) => {
    const on = railSet.has(source.name)
    if (stateFilter === 'on' && !on) return false
    if (stateFilter === 'off' && on) return false
    if (needle) {
      const haystack = `${source.displayName} ${source.name} ${sourceHost(source.baseUrl)}`.toLowerCase()
      if (!haystack.includes(needle)) return false
    }
    if (adultOnly && source.rating !== 'adult') return false
    if (contentFilter.length && !contentFilter.some((c) => source.content?.includes(c))) return false
    if (languageFilter && !(source.supportedLanguages ?? []).some((code) => baseLanguage(code) === languageFilter)) {
      return false
    }
    return true
  })
  const enabledRows = visible
    .filter((source) => railSet.has(source.name))
    .sort((a, b) => rail.indexOf(a.name) - rail.indexOf(b.name))
  const disabledRows = visible.filter((source) => !railSet.has(source.name))
  const activeFilters = contentFilter.length + (adultOnly ? 1 : 0) + (languageFilter ? 1 : 0)

  const initialSet = new Set(initialRail)
  const turnedOn = rail.filter((name) => !initialSet.has(name)).length
  const turnedOff = initialRail.filter((name) => !railSet.has(name)).length
  const orderChanged =
    initialRail.filter((name) => railSet.has(name)).join() !== rail.filter((name) => initialSet.has(name)).join()
  const dirty = turnedOn > 0 || turnedOff > 0 || orderChanged

  function toggle(name: string, on: boolean) {
    setRail((current) => (on ? [...current.filter((n) => n !== name), name] : current.filter((n) => n !== name)))
  }

  function resetToDefaults() {
    if (!sources) return
    setRail(sources.filter((s) => s.defaultEnabled ?? railSet.has(s.name)).map((s) => s.name))
  }

  function clearFilters() {
    setContentFilter([])
    setAdultOnly(false)
    setLanguageFilter(null)
  }

  function close() {
    save.reset()
    setConfirmDiscard(false)
    onClose()
  }

  // Escape and click-outside route through the Modal's own onClose, same as Cancel, so a dirty reorder can't slip out unconfirmed.
  function requestClose() {
    if (dirty) {
      setConfirmDiscard(true)
    } else {
      close()
    }
  }

  function submit() {
    // Disabled sources keep their stored index so an off/on cycle restores their rank; the enabled
    // slots are refilled in rail order, and anything the stored order never had goes last.
    const stored = priority?.order ?? []
    const storedSet = new Set(stored)
    const queue = rail.filter((name) => storedSet.has(name))
    const order = stored.map((name) => (railSet.has(name) ? queue.shift()! : name))
    for (const name of [...rail, ...(sources ?? []).map((s) => s.name)]) {
      if (!storedSet.has(name) && !order.includes(name)) order.push(name)
    }
    save.mutate(
      { order, disabled: order.filter((name) => !railSet.has(name)) },
      {
        onSuccess: () => {
          notifications.show({ message: now`Saved`, color: 'var(--ok)' })
          close()
        },
        onError: (e) => notifications.show({ message: e.message, color: 'var(--danger)' }),
      },
    )
  }

  const total = sources?.length ?? 0
  const row = (source: SourceInfo) => {
    const on = railSet.has(source.name)
    const label = source.displayName
    return (
      <div key={source.name} className="source-row" data-off={!on || undefined}>
        <SourceIcon name={source.name} label={label} size={36} />
        <div style={{ minWidth: 0 }}>
          <div className="source-row-name">
            <span>{label}</span>
            <span className="source-row-host">{sourceHost(source.baseUrl)}</span>
          </div>
          <SourceTags source={source} contentLabels={contentLabels} />
        </div>
        <Switch
          checked={on}
          onChange={(e) => toggle(source.name, e.currentTarget.checked)}
          aria-label={t`Enable ${label}`}
        />
      </div>
    )
  }

  const enabledCount = rail.length
  const catalogue = (
    <section className="source-manager-catalogue">
      <div className="source-manager-tools">
        <TextInput
          ref={searchRef}
          type="search"
          value={search}
          onChange={(e) => setSearch(e.currentTarget.value)}
          placeholder={plural(total, { one: 'Search # source', other: 'Search # sources' })}
          aria-label={t`Search sources`}
          leftSection={<IconSearch size={14} />}
          rightSection={narrow ? undefined : <Kbd size="xs">/</Kbd>}
          style={{ flex: '1 1 200px', minWidth: 160 }}
        />
        <SegmentedControl
          value={stateFilter}
          onChange={(value) => setStateFilter(value as StateFilter)}
          data={[
            { value: 'all', label: t`All` },
            { value: 'on', label: t`Enabled` },
            { value: 'off', label: t`Disabled` },
          ]}
        />
        <Popover opened={filtersOpen} onChange={setFiltersOpen} position="bottom-end" width={420} shadow="md">
          <Popover.Target>
            <Button
              variant="default"
              leftSection={<IconFilter size={14} />}
              rightSection={activeFilters > 0 ? <span className="source-filter-count">{activeFilters}</span> : undefined}
              onClick={() => setFiltersOpen((o) => !o)}
              aria-expanded={filtersOpen}
            >
              <Trans>Filters</Trans>
            </Button>
          </Popover.Target>
          <Popover.Dropdown className="source-filter-pop">
            <div>
              <div className="source-filter-label"><Trans>Content</Trans></div>
              <Chip.Group multiple value={contentFilter} onChange={(v) => setContentFilter(v as SourceContent[])}>
                <Group gap={6}>
                  {CONTENT_ORDER.map((c) => (
                    <Chip key={c} value={c} size="xs" variant="light">{contentLabels[c]}</Chip>
                  ))}
                </Group>
              </Chip.Group>
            </div>
            <div>
              <div className="source-filter-label"><Trans>Rating</Trans></div>
              <Chip
                size="xs" variant="light" color="var(--danger)"
                checked={adultOnly} onChange={setAdultOnly}
              >
                <Trans>18+ only</Trans>
              </Chip>
            </div>
            {languageCounts.length > 0 && (
              <div>
                <div className="source-filter-label"><Trans>Language</Trans></div>
                <Group gap={6}>
                  {languageCounts.map(([code, count]) => (
                    <Chip
                      key={code} size="xs" variant="light"
                      checked={languageFilter === code}
                      onChange={(checked) => setLanguageFilter(checked ? code : null)}
                    >
                      {nameOfLanguage(code)}
                      <span className="source-filter-n">{count}</span>
                    </Chip>
                  ))}
                </Group>
              </div>
            )}
            <Group justify="space-between" className="source-filter-foot">
              <Button variant="subtle" color="gray" size="xs" onClick={clearFilters} disabled={activeFilters === 0}>
                <Trans>Clear filters</Trans>
              </Button>
              <Button variant="default" size="xs" onClick={() => setFiltersOpen(false)}>
                <Trans>Done</Trans>
              </Button>
            </Group>
          </Popover.Dropdown>
        </Popover>
      </div>
      <div className="source-manager-list">
        {enabledRows.length > 0 && (
          <>
            <div className="source-manager-group">
              <span><Trans>Enabled</Trans></span>
              <span>{enabledRows.length}</span>
            </div>
            {enabledRows.map(row)}
          </>
        )}
        {disabledRows.length > 0 && (
          <>
            <div className="source-manager-group">
              <span><Trans>Disabled</Trans></span>
              <span>{disabledRows.length}</span>
            </div>
            {disabledRows.map(row)}
          </>
        )}
        {visible.length === 0 && sources && (
          <div className="source-manager-empty">
            <Trans>No sources match. Clear a filter or try another name.</Trans>
          </div>
        )}
      </div>
    </section>
  )

  const priorityRail = (
    <aside className="source-rail">
      <div className="source-rail-head">
        <Group justify="space-between" gap="xs">
          <Text size="sm" fw={600} c="var(--ink-hi)"><Trans>Priority</Trans></Text>
          <Text size="xs" c="var(--ink-4)">
            <Plural value={enabledCount} one="# enabled" other="# enabled" />
          </Text>
        </Group>
        <Text size="xs" c="var(--ink-3)" mt={4}>
          <Trans>Top source wins when several carry the same series.</Trans>
        </Text>
      </div>
      <div className="source-rail-list">
        <PriorityRail items={rail} onChange={setRail} labelOf={labelOf} />
      </div>
      <div className="source-rail-foot">
        <IconArrowUp size={13} />
        <Trans>Newly enabled sources join at the bottom.</Trans>
      </div>
    </aside>
  )

  return (
    <>
    <Modal
      opened={opened}
      onClose={requestClose}
      size={980}
      closeOnEscape={!filtersOpen}
      fullScreen={narrow}
      closeButtonProps={{ 'aria-label': t`Close` }}
      styles={{
        content: narrow ? undefined : { height: 'min(860px, 92dvh)' },
        body: { padding: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden' },
      }}
      title={
        <div>
          <Text fw={600}><Trans>Manage sources</Trans></Text>
          <Text size="sm" c="var(--ink-3)">
            <Trans>Turn sources on or off on the left. Drag the rail on the right to set which one Maki tries first.</Trans>
          </Text>
        </div>
      }
    >
      <div className="source-manager" data-narrow={narrow || undefined}>
        {narrow && (
          <Tabs value={pane} onChange={(v) => setPane(v === 'priority' ? 'priority' : 'catalogue')}>
            <Tabs.List grow>
              <Tabs.Tab value="catalogue"><Trans>All sources</Trans></Tabs.Tab>
              <Tabs.Tab value="priority"><Trans>Priority</Trans></Tabs.Tab>
            </Tabs.List>
          </Tabs>
        )}
        <div className="source-manager-body">
          {(!narrow || pane === 'catalogue') && catalogue}
          {(!narrow || pane === 'priority') && priorityRail}
        </div>
        <footer className="source-manager-foot">
          <DirtySummary turnedOn={turnedOn} turnedOff={turnedOff} orderChanged={orderChanged} />
          <Group gap="xs" ml="auto">
            <Button variant="subtle" color="gray" onClick={resetToDefaults} disabled={!sources}>
              <Trans>Reset to defaults</Trans>
            </Button>
            <Button variant="default" onClick={requestClose} disabled={save.isPending}>
              <Trans>Cancel</Trans>
            </Button>
            <Button onClick={submit} disabled={!dirty || !priority} loading={save.isPending}>
              <Trans>Save changes</Trans>
            </Button>
          </Group>
        </footer>
      </div>
    </Modal>
    <ConfirmDialog
      opened={confirmDiscard}
      onClose={() => setConfirmDiscard(false)}
      title={<Trans>Discard changes?</Trans>}
      confirmLabel={<Trans>Discard</Trans>}
      onConfirm={close}
    >
      <Trans>Your source changes have not been saved. Closing now discards them.</Trans>
    </ConfirmDialog>
    </>
  )
}

function DirtySummary({ turnedOn, turnedOff, orderChanged }: {
  turnedOn: number
  turnedOff: number
  orderChanged: boolean
}) {
  const parts: ReactNode[] = []
  if (turnedOn > 0) parts.push(<Plural key="on" value={turnedOn} one="# turned on" other="# turned on" />)
  if (turnedOff > 0) parts.push(<Plural key="off" value={turnedOff} one="# turned off" other="# turned off" />)
  if (orderChanged) parts.push(<Trans key="order">Order changed</Trans>)
  return (
    <Text size="xs" c="var(--ink-3)">
      {parts.length === 0
        ? <Trans>No changes yet</Trans>
        : parts.map((part, i) => (
          <span key={i}>
            {i > 0 && ' · '}
            {part}
          </span>
        ))}
    </Text>
  )
}
