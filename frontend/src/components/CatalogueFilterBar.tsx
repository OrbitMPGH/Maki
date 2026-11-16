import { useLayoutEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { Trans, useLingui } from '@lingui/react/macro'
import { ActionIcon, Button, Menu, NumberInput, Popover, RangeSlider, TextInput, UnstyledButton, VisuallyHidden } from '@mantine/core'
import { useDebouncedValue, useElementSize } from '@mantine/hooks'
import {
  IconActivity,
  IconCheck,
  IconSearch,
  IconBook,
  IconCalendar,
  IconChevronDown,
  IconDeviceFloppy,
  IconDots,
  IconPlus,
  IconRuler,
  IconShield,
  IconStar,
  IconTag,
  IconUsers,
  type Icon,
} from '../lib/icons'
import {
  allowedContentRatings,
  CONTENT_RATING_LABELS,
  useMetadataSearch,
  useSeries,
  type CatalogueRule,
} from '../api/hooks'
import { useLibraryCoverLookup } from '../api/seriesLookup'
import { useAuth } from '../auth/AuthProvider'
import { useLabel } from '../i18n-context'
import { formatFixedDecimal } from '../format'
import { liveRules, TermFilters } from './CatalogueRules'
import { CreditPicker } from './CreditPicker'
import { HiddenContentMenuItem, HiddenContentModal } from './DiscoverPresets'
import { MinRatingSlider } from './MinRatingSlider'
import { DrillBack, DrillRow, useDrillPages } from './ui/DrillPanel'
import { DrillPanes } from './ui/DrillPanes'
import { contentRatingVisual, seriesStatusVisual, statusColor } from './ui/status'
import {
  CHAPTER_MAX,
  CHAPTER_MIN,
  YEAR_MAX,
  YEAR_MIN,
  useGenreOptions,
  useStatusOptions,
  useTypeOptions,
  type CatalogueFilterControls,
} from './CatalogueFilters'

/** One button on the bar and the editor its popover holds. */
export interface FilterSlot {
  id: string
  icon: Icon
  /** The button's name; short, since the bar has to fit a row. */
  label: string
  /** The popover's heading, when it should say more than the button. */
  title?: string
  /** What the button shows once the filter is set; null while it is unset. */
  value: ReactNode
  /** Shown in place of the icon once set, such as a seed's cover. */
  thumb?: ReactNode
  width?: number
  onClear: () => void
  editor: ReactNode
}

/**
 * The catalogue filters as one row of buttons, set ones first, each opening its editor in a
 * popover. When the row runs out of width the unset filters fold into one "Add filter" button,
 * which opens them as a drill panel. Order is frozen while a popover is open, so the button under
 * the pointer does not jump when its filter becomes set.
 */
export function CatalogueFilterBar({
  slots,
  isCustomized,
  onReset,
  saved,
  count,
  onSaveAsDefault,
  savingDefault = false,
}: {
  slots: FilterSlot[]
  isCustomized: boolean
  onReset: () => void
  /** The saved-filters menu. */
  saved?: ReactNode
  /** The live match count. */
  count?: ReactNode
  /** Saving an untouched bar clears the stored default, so the item reads "Clear default" then. */
  onSaveAsDefault?: () => void
  savingDefault?: boolean
}) {
  const { t } = useLingui()
  const [hiddenOpen, setHiddenOpen] = useState(false)
  const [openId, setOpenId] = useState<string | null>(null)
  const setKey = slots.filter((s) => s.value != null).map((s) => s.id).join(',')
  const [frozenKey, setFrozenKey] = useState(setKey)
  if (openId === null && frozenKey !== setKey) setFrozenKey(setKey)
  const setIds = new Set(frozenKey.split(',').filter(Boolean))
  const leading = slots.filter((s) => setIds.has(s.id))
  const trailing = slots.filter((s) => !setIds.has(s.id))

  // How many unset buttons fit after the set ones; the rest go behind "Add filter". Widths are
  // remembered from the last time each button was drawn, since a folded one is not on the page.
  const { ref: sizeRef } = useElementSize<HTMLDivElement>()
  const rowRef = useRef<HTMLDivElement | null>(null)
  const widths = useRef<Record<string, number>>({})
  const [shown, setShown] = useState(trailing.length)
  useLayoutEffect(() => {
    const row = rowRef.current
    if (!row || openId !== null) return
    row.querySelectorAll<HTMLElement>('[data-slot]').forEach((el) => {
      widths.current[el.dataset.slot!] = el.offsetWidth
    })
    const widthOf = (id: string) => (widths.current[id] ?? 110) + BAR_GAP
    let used = leading.reduce((sum, s) => sum + widthOf(s.id), 0) + DIVIDER_WIDTH
    let fit = 0
    for (let i = 0; i < trailing.length; i++) {
      const needsAdd = i < trailing.length - 1
      if (used + widthOf(trailing[i].id) + (needsAdd ? ADD_WIDTH : 0) > row.clientWidth) break
      used += widthOf(trailing[i].id)
      fit++
    }
    setShown((prev) => (prev === fit ? prev : fit))
  })
  const visibleTrailing = trailing.slice(0, shown)
  const folded = trailing.slice(shown)

  const button = (slot: FilterSlot) => (
    <FilterButton
      key={slot.id}
      slot={slot}
      opened={openId === slot.id}
      onOpen={() => setOpenId(slot.id)}
      onClose={() => setOpenId((id) => (id === slot.id ? null : id))}
    />
  )

  return (
    <div className="filter-bar">
      <div
        ref={(node) => {
          rowRef.current = node
          sizeRef(node)
        }}
        className="filter-bar-row"
        data-folded={folded.length > 0 || undefined}
        role="toolbar"
        aria-label={t`Filters`}
      >
        {leading.map(button)}
        {leading.length > 0 && visibleTrailing.length > 0 && <span className="filter-bar-divider" aria-hidden />}
        {visibleTrailing.map(button)}
        {folded.length > 0 && (
          <AddFilterButton
            slots={folded}
            opened={openId === ADD_ID}
            onOpen={() => setOpenId(ADD_ID)}
            onClose={() => setOpenId((id) => (id === ADD_ID ? null : id))}
          />
        )}
      </div>
      <div className="filter-bar-end">
        {count}
        {saved}
        {isCustomized && (
          <Button variant="subtle" size="xs" onClick={onReset}>
            <Trans>Reset</Trans>
          </Button>
        )}
        <Menu position="bottom-end" withinPortal>
          <Menu.Target>
            <ActionIcon variant="subtle" color="var(--ink-2)" aria-label={t`More filter actions`}>
              <IconDots size={18} />
            </ActionIcon>
          </Menu.Target>
          <Menu.Dropdown>
            {onSaveAsDefault && (
              <Menu.Item
                leftSection={<IconDeviceFloppy size={16} />}
                disabled={savingDefault}
                onClick={onSaveAsDefault}
              >
                {isCustomized ? <Trans>Save as default</Trans> : <Trans>Clear default</Trans>}
              </Menu.Item>
            )}
            <HiddenContentMenuItem onOpen={() => setHiddenOpen(true)} />
          </Menu.Dropdown>
        </Menu>
      </div>
      {hiddenOpen && <HiddenContentModal onClose={() => setHiddenOpen(false)} />}
    </div>
  )
}

const ADD_ID = '__add'
const BAR_GAP = 6
const DIVIDER_WIDTH = 15
const ADD_WIDTH = 120

function popoverWidth(width = 340) {
  return `min(${width}px, calc(100vw - 32px))`
}

function FilterButton({
  slot,
  opened,
  onOpen,
  onClose,
}: {
  slot: FilterSlot
  opened: boolean
  onOpen: () => void
  onClose: () => void
}) {
  const set = slot.value != null
  const SlotIcon = slot.icon
  return (
    <Popover
      opened={opened}
      onChange={(o) => (o ? onOpen() : onClose())}
      position="bottom-start"
      width={popoverWidth(slot.width)}
      trapFocus
      returnFocus
      withinPortal
    >
      <Popover.Target>
        <UnstyledButton
          className="filter-bar-button"
          data-slot={slot.id}
          data-active={set || undefined}
          data-open={opened || undefined}
          aria-expanded={opened}
          onClick={() => (opened ? onClose() : onOpen())}
        >
          {set && slot.thumb ? slot.thumb : <SlotIcon size={16} aria-hidden className="filter-bar-button-icon" />}
          {set ? (
            <span className="filter-bar-button-value">
              <VisuallyHidden>{slot.label}: </VisuallyHidden>
              {slot.value}
            </span>
          ) : (
            <span>{slot.label}</span>
          )}
          <IconChevronDown size={14} aria-hidden className="filter-bar-button-chevron" />
        </UnstyledButton>
      </Popover.Target>
      <Popover.Dropdown className="filter-bar-pop">
        <FilterEditorHead slot={slot} />
        <div className="filter-bar-pop-body">{slot.editor}</div>
      </Popover.Dropdown>
    </Popover>
  )
}

function FilterEditorHead({ slot }: { slot: FilterSlot }) {
  const SlotIcon = slot.icon
  return (
    <div className="filter-bar-pop-head">
      <SlotIcon size={16} aria-hidden className="filter-bar-button-icon" />
      <span className="filter-bar-pop-title">{slot.title ?? slot.label}</span>
      {slot.value != null && (
        <Button variant="subtle" size="compact-xs" color="var(--ink-2)" onClick={slot.onClear}>
          <Trans>Clear</Trans>
        </Button>
      )}
    </div>
  )
}

function AddFilterButton({
  slots,
  opened,
  onOpen,
  onClose,
}: {
  slots: FilterSlot[]
  opened: boolean
  onOpen: () => void
  onClose: () => void
}) {
  const { t } = useLingui()
  const parents = useMemo(
    () => Object.fromEntries([['root', 'root'], ...slots.map((s) => [s.id, 'root'])]) as Record<string, string>,
    [slots],
  )
  const drill = useDrillPages<string>('root', parents)
  const current = slots.find((s) => s.id === drill.page)
  const close = () => {
    onClose()
    drill.reset()
  }
  const width = current?.width ?? 320
  return (
    <Popover
      opened={opened}
      onChange={(o) => (o ? onOpen() : close())}
      position="bottom-start"
      width={popoverWidth(Math.max(width, 320))}
      trapFocus
      returnFocus
      withinPortal
    >
      <Popover.Target>
        <UnstyledButton
          className="filter-bar-button filter-bar-add"
          data-open={opened || undefined}
          aria-expanded={opened}
          onClick={() => (opened ? close() : onOpen())}
        >
          <IconPlus size={16} aria-hidden />
          <span>
            <Trans>Add filter</Trans>
          </span>
        </UnstyledButton>
      </Popover.Target>
      <Popover.Dropdown className="filter-bar-pop filter-bar-pop-drill">
        <DrillPanes page={drill.page} depth={drill.depth}>
          {current ? (
            <div>
              <DrillBack ref={drill.backRef} title={current.title ?? current.label} onBack={drill.back} />
              <div className="filter-bar-pop-body">{current.editor}</div>
            </div>
          ) : (
            <div className="filter-bar-drill-rows">
              {slots.map((s) => (
                <DrillRow
                  key={s.id}
                  ref={drill.rowRef(s.id)}
                  icon={s.icon}
                  label={s.title ?? s.label}
                  value={s.value ?? t`Any`}
                  active={s.value != null}
                  onClick={() => drill.open(s.id)}
                />
              ))}
            </div>
          )}
        </DrillPanes>
      </Popover.Dropdown>
    </Popover>
  )
}

/** Multi-select as toggle chips, each with an optional colour dot. */
export function ToggleChips({
  label,
  options,
  value,
  onChange,
}: {
  label: string
  options: { value: string; label: string; color?: string }[]
  value: string[]
  onChange: (value: string[]) => void
}) {
  return (
    <div className="tag-chips" role="group" aria-label={label}>
      {options.map((o) => {
        const on = value.includes(o.value)
        return (
          <button
            key={o.value}
            type="button"
            className="tag-chip filter-chip"
            data-interactive
            data-on={on || undefined}
            aria-pressed={on}
            onClick={() => onChange(on ? value.filter((v) => v !== o.value) : [...value, o.value])}
          >
            <span className="tag-chip-body">
              {o.color && <span className="filter-chip-dot" style={{ background: o.color }} aria-hidden />}
              {o.label}
            </span>
          </button>
        )
      })}
    </div>
  )
}

interface RangePreset {
  label: string
  value: [number, number]
}

/** A range slider with exact inputs under it and a few common ranges as chips. */
function RangeEditor({
  min,
  max,
  step = 1,
  value,
  onChange,
  fromLabel,
  toLabel,
  presets,
}: {
  min: number
  max: number
  step?: number
  value: [number, number]
  onChange: (value: [number, number]) => void
  fromLabel: string
  toLabel: string
  presets: RangePreset[]
}) {
  const clamp = (v: number) => Math.min(max, Math.max(min, v))
  return (
    <div className="filter-range">
      <RangeSlider
        min={min}
        max={max}
        step={step}
        minRange={0}
        value={value}
        onChange={onChange}
        thumbFromLabel={fromLabel}
        thumbToLabel={toLabel}
        label={null}
      />
      <div className="filter-range-inputs">
        <NumberInput
          size="xs"
          aria-label={fromLabel}
          min={min}
          max={max}
          value={value[0]}
          onChange={(v) => typeof v === 'number' && onChange([clamp(Math.min(v, value[1])), value[1]])}
        />
        <NumberInput
          size="xs"
          aria-label={toLabel}
          min={min}
          max={max}
          value={value[1]}
          onChange={(v) => typeof v === 'number' && onChange([value[0], clamp(Math.max(v, value[0]))])}
        />
      </div>
      <div className="tag-chips filter-presets">
        {presets.map((p) => {
          const on = p.value[0] === value[0] && p.value[1] === value[1]
          return (
            <button
              key={p.label}
              type="button"
              className="tag-chip filter-chip"
              data-interactive
              data-on={on || undefined}
              aria-pressed={on}
              onClick={() => onChange(p.value)}
            >
              <span className="tag-chip-body">{p.label}</span>
            </button>
          )
        })}
      </div>
    </div>
  )
}

const RATING_PRESETS = [0, 7, 8, 9]

function RatingEditor({ value, onChange }: { value: number; onChange: (value: number) => void }) {
  const { t } = useLingui()
  return (
    <div>
      <MinRatingSlider value={value} onChange={onChange} heading={false} />
      <div className="tag-chips filter-presets">
        {RATING_PRESETS.map((r) => {
          const on = value === r
          return (
            <button
              key={r}
              type="button"
              className="tag-chip filter-chip"
              data-interactive
              data-on={on || undefined}
              aria-pressed={on}
              onClick={() => onChange(r)}
            >
              <span className="tag-chip-body">{r === 0 ? t`Any` : `${r}+`}</span>
            </button>
          )
        })}
      </div>
    </div>
  )
}

const STATUS_VISUAL: Record<string, string> = {
  releasing: 'Ongoing',
  completed: 'Completed',
  hiatus: 'Hiatus',
  cancelled: 'Cancelled',
}

/** The genre-and-tag rules as button text: required names, then excluded ones struck through. */
function TermsValue({ rules, renderGenre }: { rules: CatalogueRule[]; renderGenre: (name: string) => string }) {
  const names = (rule: CatalogueRule) =>
    rule.terms.map((term) => (term.kind === 'genre' ? renderGenre(term.name) : term.name))
  const kept = rules.filter((r) => r.mode !== 'none')
  const dropped = rules.filter((r) => r.mode === 'none')
  return (
    <>
      {kept.map((r) => names(r).join(r.mode === 'any' ? ' / ' : ', ')).join(', ')}
      {dropped.map((r, i) => (
        <s key={i} className="filter-bar-excluded">
          {names(r).join(', ')}
        </s>
      ))}
    </>
  )
}

/**
 * The bar's slots for the catalogue filters every surface shares. `creators` adds the creator
 * picker, which only the catalogue search and the creator page carry.
 */
export function useCatalogueFilterSlots(
  controls: CatalogueFilterControls,
  { creators = false }: { creators?: boolean } = {},
): FilterSlot[] {
  const { t, i18n } = useLingui()
  const { me } = useAuth()
  const renderLabel = useLabel()
  const typeOptions = useTypeOptions()
  const statusOptions = useStatusOptions()
  const {
    terms,
    types, setTypes,
    statuses, setStatuses,
    years, setYears,
    chapters, setChapters,
    minRating, setMinRating,
    contentRatings, setContentRatings,
    credits, setCredits,
  } = controls

  const contentRatingOptions = useMemo(
    () =>
      allowedContentRatings(me?.maxContentRating).map((value) => {
        const visual = contentRatingVisual(value)
        return {
          value,
          label: renderLabel(CONTENT_RATING_LABELS[value]),
          color: visual ? statusColor(visual.color) : undefined,
        }
      }),
    [me?.maxContentRating, renderLabel, i18n.locale],
  )
  const statusChips = useMemo(
    () =>
      statusOptions.map((o) => ({
        ...o,
        color: statusColor(seriesStatusVisual(STATUS_VISUAL[o.value] ?? '').color),
      })),
    [statusOptions],
  )
  const labelsOf = (options: { value: string; label: string }[], values: string[]) =>
    values.map((v) => options.find((o) => o.value === v)?.label ?? v).join(', ')

  const rules = liveRules(terms.state)
  const thisYear = YEAR_MAX - 1
  const chaptersSet = chapters[0] > CHAPTER_MIN || chapters[1] < CHAPTER_MAX
  const yearsSet = years[0] > YEAR_MIN || years[1] < YEAR_MAX
  const chapterMax = chapters[1] >= CHAPTER_MAX ? `${CHAPTER_MAX}+` : `${chapters[1]}`

  const slots: FilterSlot[] = [
    {
      id: 'terms',
      icon: IconTag,
      label: t`Genres`,
      title: t`Genres and tags`,
      value: rules.length > 0 ? <GenreTermsValue rules={rules} /> : null,
      width: 560,
      onClear: () => terms.setState({ ...terms.state, genres: {}, tagsIn: [], tagsOut: [], rules: [] }),
      editor: <TermFilters controls={terms} heading={false} />,
    },
  ]
  if (creators) {
    slots.push({
      id: 'credits',
      icon: IconUsers,
      label: t`Creators`,
      value: credits.length > 0 ? credits.map((c) => c.name).join(', ') : null,
      width: 380,
      onClear: () => setCredits([]),
      editor: <CreditPicker value={credits} onChange={setCredits} />,
    })
  }
  slots.push(
    {
      id: 'types',
      icon: IconBook,
      label: t`Type`,
      value: types.length > 0 ? labelsOf(typeOptions, types) : null,
      onClear: () => setTypes([]),
      editor: <ToggleChips label={t`Type`} options={typeOptions} value={types} onChange={setTypes} />,
    },
    {
      id: 'statuses',
      icon: IconActivity,
      label: t`Status`,
      value: statuses.length > 0 ? labelsOf(statusOptions, statuses) : null,
      onClear: () => setStatuses([]),
      editor: <ToggleChips label={t`Status`} options={statusChips} value={statuses} onChange={setStatuses} />,
    },
    {
      id: 'content',
      icon: IconShield,
      label: t`Content`,
      title: t`Content rating`,
      value: contentRatings.length > 0 ? labelsOf(contentRatingOptions, contentRatings) : null,
      onClear: () => setContentRatings([]),
      editor: (
        <ToggleChips
          label={t`Content rating`}
          options={contentRatingOptions}
          value={contentRatings}
          onChange={setContentRatings}
        />
      ),
    },
    {
      id: 'chapters',
      icon: IconRuler,
      label: t`Chapters`,
      value: chaptersSet ? `${chapters[0]}–${chapterMax}` : null,
      onClear: () => setChapters([CHAPTER_MIN, CHAPTER_MAX]),
      editor: (
        <RangeEditor
          min={CHAPTER_MIN}
          max={CHAPTER_MAX}
          step={5}
          value={chapters}
          onChange={setChapters}
          fromLabel={t`Fewest chapters`}
          toLabel={t`Most chapters`}
          presets={[
            { label: t`Under 50`, value: [CHAPTER_MIN, 50] },
            { label: t`50 to 200`, value: [50, 200] },
            { label: t`Over 200`, value: [200, CHAPTER_MAX] },
          ]}
        />
      ),
    },
    {
      id: 'years',
      icon: IconCalendar,
      label: t`Year`,
      value: yearsSet ? `${years[0]}–${years[1]}` : null,
      onClear: () => setYears([YEAR_MIN, YEAR_MAX]),
      editor: (
        <RangeEditor
          min={YEAR_MIN}
          max={YEAR_MAX}
          value={years}
          onChange={setYears}
          fromLabel={t`Earliest year`}
          toLabel={t`Latest year`}
          presets={[
            { label: t`Last 5 years`, value: [thisYear - 4, YEAR_MAX] },
            { label: t`Last 10 years`, value: [thisYear - 9, YEAR_MAX] },
            { label: t`2000s`, value: [2000, 2009] },
            { label: t`Before 2000`, value: [YEAR_MIN, 1999] },
          ]}
        />
      ),
    },
    {
      id: 'rating',
      icon: IconStar,
      label: t`Rating`,
      title: t`Minimum rating`,
      value: minRating > 0 ? `${formatFixedDecimal(minRating, 1)}+` : null,
      onClear: () => setMinRating(0),
      editor: <RatingEditor value={minRating} onChange={setMinRating} />,
    },
  )
  return slots
}

function GenreTermsValue({ rules }: { rules: CatalogueRule[] }) {
  const genres = useGenreOptions()
  const renderGenre = (name: string) =>
    genres.find((g) => g.value.toLowerCase() === name.toLowerCase())?.label ?? name
  return <TermsValue rules={rules} renderGenre={renderGenre} />
}

export interface SeedPick {
  id: string
  title: string
  cover: string | null
}

export function SeedThumb({ url }: { url: string | null }) {
  return url ? <img className="seed-thumb" src={url} alt="" loading="lazy" /> : <span className="seed-thumb" aria-hidden />
}

/**
 * Picks the series Recommended is seeded from: the current seeds first, then library and catalogue
 * matches for what is typed. A list rather than a dropdown, since it already sits in a popover.
 */
export function SeedPicker({
  value,
  labels,
  onPick,
  onRemove,
}: {
  value: string[]
  labels: Record<string, string>
  onPick: (pick: SeedPick) => void
  onRemove: (id: string) => void
}) {
  const { t } = useLingui()
  const [query, setQuery] = useState('')
  const [debounced] = useDebouncedValue(query, 300)
  const { data: library } = useSeries()
  const { data: found } = useMetadataSearch(debounced)
  const coverFor = useLibraryCoverLookup()
  const q = query.trim().toLowerCase()

  const rows = useMemo(() => {
    const picked: SeedPick[] = value.map((id) => ({ id, title: labels[id] ?? id, cover: coverFor(id) }))
    const typed = q.length >= 2
    const seen = new Set(value)
    const more: SeedPick[] = []
    for (const s of library ?? []) {
      if (more.length >= (typed ? 6 : 8)) break
      if (s.mangaBakaId == null) continue
      const id = String(s.mangaBakaId)
      if (seen.has(id) || (typed && !s.displayTitle.toLowerCase().includes(q))) continue
      seen.add(id)
      more.push({ id, title: s.displayTitle, cover: coverFor(id) })
    }
    for (const r of typed ? (found ?? []) : []) {
      if (more.length >= 10) break
      if (seen.has(r.providerId)) continue
      seen.add(r.providerId)
      more.push({ id: r.providerId, title: r.title, cover: coverFor(r.providerId) ?? r.coverUrl })
    }
    return [...picked, ...more]
  }, [value, labels, coverFor, q, library, found])

  return (
    <div className="seed-picker">
      <TextInput
        size="xs"
        data-autofocus
        leftSection={<IconSearch size={14} />}
        placeholder={t`Search series`}
        aria-label={t`Search series`}
        value={query}
        onChange={(e) => setQuery(e.currentTarget.value)}
      />
      {rows.length > 0 && (
        <div className="seed-list">
          {rows.map((row) => {
            const on = value.includes(row.id)
            return (
              <UnstyledButton
                key={row.id}
                className="seed-row"
                data-on={on || undefined}
                aria-pressed={on}
                onClick={() => (on ? onRemove(row.id) : onPick(row))}
              >
                <SeedThumb url={row.cover} />
                <span className="seed-row-title">{row.title}</span>
                {on ? <IconCheck size={16} aria-hidden /> : <IconPlus size={16} aria-hidden />}
              </UnstyledButton>
            )
          })}
        </div>
      )}
      {value.length === 0 && (
        <div className="filter-bar-hint">
          <Trans>With no series picked, your whole library is used.</Trans>
        </div>
      )}
    </div>
  )
}
