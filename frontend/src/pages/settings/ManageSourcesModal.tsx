import { useEffect, useMemo, useRef, useState, type DragEvent, type ReactNode } from 'react'
import {
  ActionIcon, Alert, Button, Chip, Group, Kbd, Loader, Modal, MultiSelect, Popover, SegmentedControl, Select, Stack,
  Switch, Tabs, Text, TextInput, Tooltip,
} from '@mantine/core'
import { useHotkeys, useMediaQuery } from '@mantine/hooks'
import { notifications } from '@mantine/notifications'
import {
  IconArrowUp, IconChevronDown, IconChevronUp, IconFilter, IconGripVertical, IconLanguage, IconPuzzle,
  IconSearch, IconShield,
} from '@tabler/icons-react'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { plural, t as now } from '@lingui/core/macro'
import {
  useSaveSourcePriority, useSourcePriority, useSources,
  type SourceContent, type SourceInfo, type SourceQualitySummary,
} from '../../api/hooks'
import { languageName } from '../../api/titles'
import { useRefreshSuwayomiSources, useSetSuwayomiSourceLanguage, useSuwayomiConnection } from '../../api/suwayomi'
import { SourceIcon, baseLanguage, sourceHost } from '../../sourceIcons'
import { ConfirmDialog } from '../../components/ui/ConfirmDialog'

type StateFilter = 'all' | 'on' | 'off'
export type ProviderFilter = 'builtin' | 'suwayomi'

/** One row in the catalogue: a source, or every language variant of one Suwayomi site. */
interface CatalogueEntry {
  key: string
  source: SourceInfo
  /** Set for a site whose extension registers one Suwayomi source per language. */
  members?: SourceInfo[]
}

/**
 * Which site a Suwayomi source is a language variant of. A mixed-language source stays on its own
 * row: it is one source with a chapter language to set, not one of several to pick from.
 */
const siteKeyOf = (source: SourceInfo) =>
  source.suwayomi && !source.suwayomi.mixedLanguage ? source.suwayomi.siteName.toLowerCase() : null

/** What the search box matches a source's site on. A Suwayomi source's base URL is the Suwayomi server. */
const hostOf = (source: SourceInfo) => (source.provider === 'suwayomi' ? 'suwayomi' : sourceHost(source.baseUrl))
type Pane = 'catalogue' | 'priority'

const CONTENT_ORDER: SourceContent[] = ['manga', 'manhwa', 'manhua', 'webtoon', 'doujinshi']

/**
 * Offered as a mixed-language Suwayomi source's chapter language alongside every language a source
 * here already publishes, so the usual ones are there even before any source carries them.
 */
const COMMON_LANGUAGES = [
  'en', 'ja', 'ko', 'zh', 'zh-Hans', 'zh-Hant', 'es', 'es-419', 'pt', 'pt-BR', 'fr', 'de', 'it', 'ru', 'pl',
  'tr', 'uk', 'id', 'vi', 'th', 'ar',
]
/** What the server accepts as a chapter language; Mihon's `all`/`multi`/`other` fail it. */
const LANGUAGE_CODE = /^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$/
const MIXED_LANGUAGE = new Set(['all', 'multi', 'other'])

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

function QualityTag({ quality }: { quality: SourceQualitySummary }) {
  const { t, i18n } = useLingui()
  const bpp = i18n.number(quality.bitsPerPixel, { maximumFractionDigits: 2 })
  const samples = quality.samples
  const series = quality.series
  const label = {
    high: t`High quality`,
    good: t`Good quality`,
    fair: t`Fair quality`,
    low: t`Low quality`,
  }[quality.rating]
  const basis =
    quality.basis === 'library'
      ? plural(samples, {
          one: `Measured from # chapter across ${series} series in your library: ${bpp} bits per pixel, JPG equivalent.`,
          other: `Measured from # chapters across ${series} series in your library: ${bpp} bits per pixel, JPG equivalent.`,
        })
      : t`Measured on a sample library, not yet on yours: ${bpp} bits per pixel, JPG equivalent. Sharper, less compressed pages score higher.`
  return (
    <Tooltip label={basis} events={{ hover: true, focus: true, touch: true }} withArrow multiline w={280}>
      <span className="source-tag" data-tone={`quality-${quality.rating}`} tabIndex={0}>
        {label}
      </span>
    </Tooltip>
  )
}

function SourceTags({ source, contentLabels, nameOfLanguage }: {
  source: SourceInfo
  contentLabels: Record<SourceContent, string>
  nameOfLanguage: (code: string) => string
}) {
  const languages = source.supportedLanguages ?? []
  const languageCount = new Set(languages.map(baseLanguage)).size
  return (
    <div className="source-tags">
      {source.suwayomi?.missing && (
        <span className="source-tag" data-tone="adult"><Trans>No longer in Suwayomi</Trans></span>
      )}
      {source.suwayomi?.needsSetup && (
        <span className="source-tag" data-tone="setup"><Trans>Several languages, pick one</Trans></span>
      )}
      {source.kind === 'official' && <span className="source-tag" data-tone="official"><Trans>Official</Trans></span>}
      {source.kind === 'scanlator' && <span className="source-tag" data-tone="kind"><Trans>Scanlator</Trans></span>}
      {source.kind === 'aggregator' && <span className="source-tag" data-tone="kind"><Trans>Aggregator</Trans></span>}
      {(source.content ?? []).map((c) => contentLabels[c] && (
        <span key={c} className="source-tag">{contentLabels[c]}</span>
      ))}
      {source.quality && <QualityTag quality={source.quality} />}
      {source.rating === 'adult' && <span className="source-tag" data-tone="adult"><Trans>18+</Trans></span>}
      {source.rating === 'mature' && <span className="source-tag" data-tone="mature"><Trans>Mature</Trans></span>}
      {languageCount > 3 ? (
        <Tooltip
          label={[...new Set(languages.map(nameOfLanguage))].sort((a, b) => a.localeCompare(b)).join(', ')}
          events={{ hover: true, focus: true, touch: true }}
          withArrow
          multiline
          w={280}
        >
          <span className="source-tag" data-tone="languages" tabIndex={0}>
            <Plural value={languageCount} one="# language" other="# languages" />
          </span>
        </Tooltip>
      ) : (
        languages.map((code) => (
          <Tooltip key={code} label={nameOfLanguage(code)} events={{ hover: true, focus: true, touch: true }} withArrow>
            <span className="source-tag" data-tone="language" tabIndex={0}>{code}</span>
          </Tooltip>
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
                variant="subtle" color="var(--neutral)" size="sm" disabled={i === 0}
                aria-label={t`Move ${label} up`}
                onClick={() => move(i, -1)}
              >
                <IconChevronUp size={14} />
              </ActionIcon>
              <ActionIcon
                variant="subtle" color="var(--neutral)" size="sm" disabled={i === items.length - 1}
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
 * The chapter language of a Suwayomi source that mixes languages. Mihon chapters carry no language,
 * so every chapter from it is filed under this one, and the source stays off until one is picked.
 */
function SuwayomiLanguageSetup({ source, options, opened, onToggle, onSaved }: {
  source: SourceInfo
  options: { value: string; label: string }[]
  opened: boolean
  onToggle: (open: boolean) => void
  onSaved: () => void
}) {
  const { t } = useLingui()
  const save = useSetSuwayomiSourceLanguage()
  const current = source.suwayomi?.chapterLanguage ?? null
  const needsSetup = source.suwayomi?.needsSetup ?? false
  const [value, setValue] = useState<string | null>(current)
  const label = source.displayName

  useEffect(() => {
    if (opened) setValue(current)
  }, [opened, current])

  return (
    <Popover opened={opened} onChange={onToggle} position="bottom-end" width={300} shadow="md" withArrow trapFocus>
      <Popover.Target>
        {needsSetup ? (
          <Button
            size="compact-xs"
            variant="light"
            onClick={() => onToggle(!opened)}
            aria-expanded={opened}
          >
            <Trans>Set up</Trans>
          </Button>
        ) : (
          <ActionIcon
            variant="subtle"
            color="var(--neutral)"
            size="sm"
            onClick={() => onToggle(!opened)}
            aria-expanded={opened}
            aria-label={t`Change the chapter language of ${label}`}
          >
            <IconLanguage size={15} />
          </ActionIcon>
        )}
      </Popover.Target>
      <Popover.Dropdown>
        <Stack gap="xs">
          <Text size="sm" fw={600} c="var(--ink-hi)"><Trans>Chapter language</Trans></Text>
          <Text size="xs" c="var(--ink-3)">
            <Trans>
              {label} mixes languages, and Suwayomi does not say which one a chapter is in. Every
              chapter from it is filed under the language you pick here.
            </Trans>
          </Text>
          <Select
            data={options}
            value={value}
            onChange={setValue}
            searchable
            openOnFocus={false}
            selectFirstOptionOnChange
            placeholder={t`Pick a language`}
            aria-label={t`Chapter language for ${label}`}
            nothingFoundMessage={t`No language matches`}
            maxDropdownHeight={220}
            comboboxProps={{ withinPortal: false }}
          />
          <Group justify="flex-end" gap="xs">
            <Button size="xs" variant="default" onClick={() => onToggle(false)}>
              <Trans>Cancel</Trans>
            </Button>
            <Button
              size="xs"
              disabled={!value || value === current}
              loading={save.isPending}
              onClick={() => {
                if (!value || !source.suwayomi) return
                save.mutate({ id: source.suwayomi.sourceId, chapterLanguage: value }, {
                  onSuccess: () => {
                    onToggle(false)
                    if (needsSetup) onSaved()
                  },
                })
              }}
            >
              <Trans>Save</Trans>
            </Button>
          </Group>
        </Stack>
      </Popover.Dropdown>
    </Popover>
  )
}

/**
 * The languages of a Suwayomi site whose extension registers one source per language, as MangaDex
 * does. Each pick is still its own source underneath, with its own chapters and its own place on the
 * priority rail; this only spares the catalogue forty rows of the same site.
 */
function SuwayomiLanguagePicker({ siteName, members, selected, onChange, opened, onToggle, nameOfLanguage }: {
  siteName: string
  members: SourceInfo[]
  selected: string[]
  onChange: (names: string[]) => void
  opened: boolean
  onToggle: (open: boolean) => void
  nameOfLanguage: (code: string) => string
}) {
  const { t } = useLingui()
  const gone = t`no longer in Suwayomi`
  const options = members
    .map((member) => {
      const code = member.suwayomi?.lang ?? member.supportedLanguages?.[0] ?? member.name
      const label = `${nameOfLanguage(code)} (${code})`
      return { value: member.name, label: member.suwayomi?.missing ? `${label}, ${gone}` : label }
    })
    .sort((a, b) => a.label.localeCompare(b.label))

  return (
    <Popover opened={opened} onChange={onToggle} position="bottom-end" width={320} shadow="md" withArrow trapFocus>
      <Popover.Target>
        <Button
          size="compact-xs"
          variant="light"
          onClick={() => onToggle(!opened)}
          aria-expanded={opened}
        >
          <Trans>Languages</Trans>
        </Button>
      </Popover.Target>
      <Popover.Dropdown>
        <Stack gap="xs">
          <Text size="sm" fw={600} c="var(--ink-hi)"><Trans>{siteName} languages</Trans></Text>
          <Text size="xs" c="var(--ink-3)">
            <Trans>
              Suwayomi has a separate source for each language of this site, with its own chapters.
              Every language you pick is searched for each series you add and gets its own place in
              the priority list.
            </Trans>
          </Text>
          <MultiSelect
            data={options}
            value={selected}
            onChange={onChange}
            searchable
            clearable
            openOnFocus={false}
            selectFirstOptionOnChange
            hidePickedOptions
            placeholder={selected.length === 0 ? t`Pick languages` : undefined}
            aria-label={t`Languages of ${siteName}`}
            nothingFoundMessage={t`No language matches`}
            maxDropdownHeight={220}
            comboboxProps={{ withinPortal: false }}
          />
          <Group justify="flex-end">
            <Button size="xs" variant="default" onClick={() => onToggle(false)}>
              <Trans>Done</Trans>
            </Button>
          </Group>
        </Stack>
      </Popover.Dropdown>
    </Popover>
  )
}

/** Suwayomi's mark on a row, in place of the host a built-in source shows. */
function SuwayomiBadge() {
  return (
    <span className="source-provider" data-provider="suwayomi">
      <IconPuzzle size={12} stroke={2.2} />
      Suwayomi
    </span>
  )
}

const REFRESH_WAIT_MS = 4000

/**
 * Every registered source: switch them on or off in the catalogue and rank the enabled ones on the
 * rail. Edits stay local until Save, which writes the whole thing through the priority endpoint
 * once. Disabled sources are appended after the ranked ones, so a source switched back on later
 * joins at the bottom, the same as it does here. With Suwayomi connected, opening this first reads
 * its installed sources, so an extension installed since shows up here, switched off.
 */
export function ManageSourcesModal({ opened, onClose, initialProvider = null }: {
  opened: boolean
  onClose: () => void
  /** Opens with the provider filter already set, as the Suwayomi card does. */
  initialProvider?: ProviderFilter | null
}) {
  const { t } = useLingui()
  const narrow = useMediaQuery('(max-width: 760px)') ?? false
  const { data: sources, isFetching: sourcesFetching } = useSources()
  const { data: priority, isFetching: priorityFetching } = useSourcePriority(opened)
  const save = useSaveSourcePriority()
  const { data: suwayomi, isPending: suwayomiPending } = useSuwayomiConnection(opened)
  const refresh = useRefreshSuwayomiSources()
  const refreshSources = refresh.mutate
  const contentLabels = useContentLabels()
  const nameOfLanguage = useLanguageNamer()

  const [rail, setRail] = useState<string[]>([])
  const [initialRail, setInitialRail] = useState<string[]>([])
  const [ready, setReady] = useState(false)
  const [refreshState, setRefreshState] = useState<'idle' | 'running' | 'done'>('idle')
  const [refreshGaveUp, setRefreshGaveUp] = useState(false)
  const [pane, setPane] = useState<Pane>('catalogue')
  const [search, setSearch] = useState('')
  const [stateFilter, setStateFilter] = useState<StateFilter>('all')
  const [contentFilter, setContentFilter] = useState<SourceContent[]>([])
  const [adultOnly, setAdultOnly] = useState(false)
  const [languageFilter, setLanguageFilter] = useState<string | null>(null)
  const [providerFilter, setProviderFilter] = useState<ProviderFilter | null>(null)
  const [filtersOpen, setFiltersOpen] = useState(false)
  const [setupFor, setSetupFor] = useState<string | null>(null)
  const [confirmDiscard, setConfirmDiscard] = useState(false)
  const searchRef = useRef<HTMLInputElement>(null)

  const suwayomiUrl = suwayomi?.url ?? null
  useEffect(() => {
    if (!opened) {
      setRefreshState('idle')
      return
    }
    if (!suwayomiUrl || refreshState !== 'idle') return
    setRefreshState('running')
    refreshSources(undefined, { onSettled: () => setRefreshState((state) => (state === 'running' ? 'done' : state)) })
  }, [opened, suwayomiUrl, refreshState, refreshSources])
  // An unreachable Suwayomi would otherwise hold the rail back for the request timeout. Past this
  // the modal seeds from what is stored, and the refresh still lands in the catalogue when it ends.
  useEffect(() => {
    if (refreshState !== 'running') {
      setRefreshGaveUp(false)
      return
    }
    const timer = window.setTimeout(() => setRefreshGaveUp(true), REFRESH_WAIT_MS)
    return () => window.clearTimeout(timer)
  }, [refreshState])
  const waitingForSuwayomi = suwayomiPending || (Boolean(suwayomiUrl) && refreshState !== 'done' && !refreshGaveUp)

  // Seed once per opening. A background refetch of the priority must not wipe a draft in progress.
  const seeded = useRef(false)
  useEffect(() => {
    if (!opened) {
      seeded.current = false
      setReady(false)
      return
    }
    // Waiting out a refetch keeps a reopen straight after a save from seeding the pre-save order,
    // and waiting out the Suwayomi refresh keeps a source it just dropped off the rail.
    if (seeded.current || !priority || priorityFetching || sourcesFetching || waitingForSuwayomi) return
    seeded.current = true
    const start = priority.order.filter((name) => !priority.disabled.includes(name))
    setRail(start)
    setInitialRail(start)
    setReady(true)
    setPane('catalogue')
    setSearch('')
    setStateFilter('all')
    setContentFilter([])
    setAdultOnly(false)
    setLanguageFilter(null)
    setProviderFilter(initialProvider)
    setSetupFor(null)
  }, [opened, priority, priorityFetching, sourcesFetching, waitingForSuwayomi, initialProvider])

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

  const hasSuwayomi = (sources ?? []).some((s) => s.provider === 'suwayomi')
  const sites = useMemo(() => {
    const bySite = new Map<string, SourceInfo[]>()
    for (const source of sources ?? []) {
      const site = siteKeyOf(source)
      if (site) bySite.set(site, [...(bySite.get(site) ?? []), source])
    }
    return new Map([...bySite].filter(([, members]) => members.length > 1))
  }, [sources])
  const languageOptions = useMemo(() => {
    const codes = new Map<string, string>()
    const candidates = [
      ...COMMON_LANGUAGES,
      ...(sources ?? []).flatMap((s) => [...(s.supportedLanguages ?? []), s.suwayomi?.lang ?? '']),
    ]
    for (const code of candidates) {
      const key = code.toLowerCase()
      if (!LANGUAGE_CODE.test(code) || MIXED_LANGUAGE.has(key) || codes.has(key)) continue
      codes.set(key, code)
    }
    return [...codes.values()]
      .map((code) => ({ value: code, label: `${nameOfLanguage(code)} (${code})` }))
      .sort((a, b) => a.label.localeCompare(b.label))
  }, [sources, nameOfLanguage])

  const needle = search.trim().toLowerCase()
  const visible = (sources ?? []).filter((source) => {
    const on = railSet.has(source.name)
    if (stateFilter === 'on' && !on) return false
    if (stateFilter === 'off' && on) return false
    if (needle) {
      const haystack = `${source.displayName} ${source.name} ${hostOf(source)}`.toLowerCase()
      if (!haystack.includes(needle)) return false
    }
    if (providerFilter === 'suwayomi' && source.provider !== 'suwayomi') return false
    if (providerFilter === 'builtin' && source.provider === 'suwayomi') return false
    if (adultOnly && source.rating !== 'adult') return false
    if (contentFilter.length && !contentFilter.some((c) => source.content?.includes(c))) return false
    if (languageFilter && !(source.supportedLanguages ?? []).some((code) => baseLanguage(code) === languageFilter)) {
      return false
    }
    return true
  })
  // A site appears once, among the enabled when any of its languages is on, at its best-ranked one.
  const toEntries = (rows: SourceInfo[]) => {
    const entries: CatalogueEntry[] = []
    const seen = new Set<string>()
    for (const source of rows) {
      const site = siteKeyOf(source)
      const members = site ? sites.get(site) : undefined
      if (!site || !members) {
        entries.push({ key: source.name, source })
      } else if (!seen.has(site)) {
        seen.add(site)
        entries.push({ key: `site:${site}`, source, members })
      }
    }
    return entries
  }
  const enabledRows = toEntries(visible
    .filter((source) => railSet.has(source.name))
    .sort((a, b) => rail.indexOf(a.name) - rail.indexOf(b.name)))
  const enabledKeys = new Set(enabledRows.map((entry) => entry.key))
  const disabledRows = toEntries(visible.filter((source) => !railSet.has(source.name)))
    .filter((entry) => !enabledKeys.has(entry.key))
  const activeFilters =
    contentFilter.length + (adultOnly ? 1 : 0) + (languageFilter ? 1 : 0) + (providerFilter ? 1 : 0)

  const initialSet = new Set(initialRail)
  const turnedOn = rail.filter((name) => !initialSet.has(name)).length
  const turnedOff = initialRail.filter((name) => !railSet.has(name)).length
  const orderChanged =
    initialRail.filter((name) => railSet.has(name)).join() !== rail.filter((name) => initialSet.has(name)).join()
  const dirty = turnedOn > 0 || turnedOff > 0 || orderChanged
  const guardUnload = opened && dirty

  useEffect(() => {
    if (!guardUnload) return
    const warn = (e: BeforeUnloadEvent) => e.preventDefault()
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [guardUnload])

  function toggle(name: string, on: boolean) {
    setRail((current) => (on ? [...current.filter((n) => n !== name), name] : current.filter((n) => n !== name)))
  }

  /** Languages picked for a site keep their rank; new ones join the bottom, like a switched-on source. */
  function pickLanguages(members: SourceInfo[], picked: string[]) {
    const memberNames = new Set(members.map((m) => m.name))
    const wanted = new Set(picked)
    setRail((current) => [
      ...current.filter((name) => !memberNames.has(name) || wanted.has(name)),
      ...picked.filter((name) => !current.includes(name)),
    ])
  }

  function resetToDefaults() {
    if (!sources) return
    setRail(
      sources
        .filter((s) => s.defaultEnabled ?? railSet.has(s.name))
        .sort((a, b) => (a.defaultRank ?? Infinity) - (b.defaultRank ?? Infinity))
        .map((s) => s.name),
    )
  }

  function clearFilters() {
    setContentFilter([])
    setAdultOnly(false)
    setLanguageFilter(null)
    setProviderFilter(null)
  }

  function close() {
    save.reset()
    refresh.reset()
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
    // A Suwayomi refresh can drop a source between seeding and saving; the server refuses unknown
    // names, so both the rail and the stored order are held to what the catalogue has now.
    const stored = (priority?.order ?? []).filter((name) => byName.has(name))
    const storedSet = new Set(stored)
    const known = rail.filter((name) => byName.has(name))
    const queue = known.filter((name) => storedSet.has(name))
    const order = stored.map((name) => (railSet.has(name) ? queue.shift()! : name))
    for (const name of [...known, ...(sources ?? []).map((s) => s.name)]) {
      if (!storedSet.has(name) && !order.includes(name)) order.push(name)
    }
    save.mutate(
      { order, disabled: order.filter((name) => !railSet.has(name)) },
      {
        onSuccess: () => {
          notifications.show({ message: now`Saved`, color: 'var(--ok)' })
          close()
        },
      },
    )
  }

  const total = sources?.length ?? 0
  const siteRow = (key: string, members: SourceInfo[]) => {
    const first = members[0]
    const siteName = first.suwayomi?.siteName ?? first.displayName
    const picked = members.filter((m) => railSet.has(m.name)).map((m) => m.name)
    const languagesOn = picked.length
    const ratings = new Set(members.map((m) => m.rating))
    // The tags speak for the site: every language it has, and its strictest rating.
    const site: SourceInfo = {
      ...first,
      supportedLanguages: members.flatMap((m) => m.supportedLanguages ?? []),
      rating: ratings.has('adult') ? 'adult' : ratings.has('mature') ? 'mature' : first.rating,
      quality: null,
    }
    return (
      <div key={key} className="source-row" data-off={languagesOn === 0 || undefined}>
        <SourceIcon name={first.name} label={siteName} size={36} />
        <div style={{ minWidth: 0 }}>
          <div className="source-row-name">
            <span>{siteName}</span>
            <SuwayomiBadge />
            <span className="source-row-host">
              <Plural value={languagesOn} _0="No languages on" one="# language on" other="# languages on" />
            </span>
          </div>
          <SourceTags source={site} contentLabels={contentLabels} nameOfLanguage={nameOfLanguage} />
        </div>
        <SuwayomiLanguagePicker
          siteName={siteName}
          members={members}
          selected={picked}
          onChange={(names) => pickLanguages(members, names)}
          opened={setupFor === key}
          onToggle={(open) => setSetupFor(open ? key : null)}
          nameOfLanguage={nameOfLanguage}
        />
      </div>
    )
  }

  const row = ({ key, source, members }: CatalogueEntry) => {
    if (members) return siteRow(key, members)
    const on = railSet.has(source.name)
    const label = source.displayName
    const needsSetup = source.suwayomi?.needsSetup ?? false
    return (
      <div key={source.name} className="source-row" data-off={!on || undefined}>
        <SourceIcon name={source.name} label={label} size={36} />
        <div style={{ minWidth: 0 }}>
          <div className="source-row-name">
            <span>{label}</span>
            {source.provider === 'suwayomi'
              ? <SuwayomiBadge />
              : <span className="source-row-host">{sourceHost(source.baseUrl)}</span>}
          </div>
          <SourceTags source={source} contentLabels={contentLabels} nameOfLanguage={nameOfLanguage} />
        </div>
        <Group gap={8} wrap="nowrap">
          {source.suwayomi?.mixedLanguage && (
            <SuwayomiLanguageSetup
              source={source}
              options={languageOptions}
              opened={setupFor === source.name}
              onToggle={(open) => setSetupFor(open ? source.name : null)}
              onSaved={() => toggle(source.name, true)}
            />
          )}
          <Tooltip
            label={t`Pick a chapter language first`}
            disabled={!needsSetup}
            events={{ hover: true, focus: true, touch: true }}
            withArrow
          >
            <span>
              <Switch
                checked={on && !needsSetup}
                disabled={needsSetup}
                onChange={(e) => toggle(source.name, e.currentTarget.checked)}
                aria-label={t`Enable ${label}`}
              />
            </span>
          </Tooltip>
        </Group>
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
          aria-label={t`Filter by state`}
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
            {hasSuwayomi && (
              <div>
                <div className="source-filter-label"><Trans>Provider</Trans></div>
                <Group gap={6}>
                  <Chip
                    size="xs" variant="light"
                    checked={providerFilter === 'builtin'}
                    onChange={(checked) => setProviderFilter(checked ? 'builtin' : null)}
                  >
                    <Trans>Built in</Trans>
                  </Chip>
                  <Chip
                    size="xs" variant="light"
                    checked={providerFilter === 'suwayomi'}
                    onChange={(checked) => setProviderFilter(checked ? 'suwayomi' : null)}
                  >
                    Suwayomi
                  </Chip>
                </Group>
              </div>
            )}
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
              <Button variant="subtle" color="var(--neutral)" size="xs" onClick={clearFilters} disabled={activeFilters === 0}>
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
        {refresh.data && !refresh.data.reachable && (
          <Alert color="yellow" variant="light" mb="xs">
            {refresh.data.error}{' '}
            <Trans>The Suwayomi sources below are the ones it listed last time.</Trans>
          </Alert>
        )}
        {!ready && (
          <Group justify="center" p="xl">
            <Loader size="sm" />
          </Group>
        )}
        {ready && enabledRows.length > 0 && (
          <>
            <div className="source-manager-group">
              <span><Trans>Enabled</Trans></span>
              <span>{enabledRows.length}</span>
            </div>
            {enabledRows.map(row)}
          </>
        )}
        {ready && disabledRows.length > 0 && (
          <>
            <div className="source-manager-group">
              <span><Trans>Disabled</Trans></span>
              <span>{disabledRows.length}</span>
            </div>
            {disabledRows.map(row)}
          </>
        )}
        {ready && visible.length === 0 && sources && (
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
          <Trans>Top source is tried first when several carry the same series.</Trans>
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
      closeOnEscape={!filtersOpen && !setupFor}
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
            <Button variant="subtle" color="var(--neutral)" onClick={resetToDefaults} disabled={!sources}>
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
