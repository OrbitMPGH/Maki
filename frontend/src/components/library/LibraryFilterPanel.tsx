import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from 'react'
import { Drawer, NumberInput, SegmentedControl } from '@mantine/core'
import { Plural, useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'
import {
  IconAdjustments,
  IconAdjustmentsHorizontal,
  IconBell,
  IconBook,
  IconBookmarks,
  IconBooks,
  IconCalendar,
  IconChecks,
  IconClock,
  IconDeviceTv,
  IconDownload,
  IconEye,
  IconEyeOff,
  IconFeather,
  IconFileText,
  IconFolder,
  IconPlugConnected,
  IconRanking,
  IconSettings,
  IconShield,
  IconSortAscendingNumbers,
  IconStar,
  IconTag,
  IconTags,
  IconWorld,
} from '../../lib/icons'
import { formatDecimal, formatNumber, formatPercent } from '../../format'
import { DrillPanes } from '../ui/DrillPanes'
import { DrillBack, DrillChoices, DrillField, DrillRow, useDrillPages, type DrillChoice } from '../ui/DrillPanel'
import {
  DrillFooter,
  DrillGroup,
  DrillMultiChoices,
  DrillNumberRange,
  DrillSearch,
  DrillSliderLines,
  DrillSliderRange,
  DrillSwitch,
  DrillTriChoices,
} from '../ui/DrillControls'
import { TagChip, TagChips } from '../ui/TagChip'

/** A choice in one of the filter lists. `name` is the bare name when `label` carries a count. */
export interface FilterOption {
  value: string
  label: string
  name?: string
}

/** Every filter the drawer edits, as the Library page keeps them. */
export interface LibraryFilterValues {
  status: string
  types: string[]
  contentRatings: string[]
  readingStatuses: string[]
  chapterMode: string
  chapterMin: number | null
  chapterMax: number | null
  yearMin: number | null
  yearMax: number | null
  addedWithin: number | null
  credits: string[]
  publishers: string[]
  anime: string
  clash: string
  tags: string[]
  tagMatch: string
  excludeTags: string[]
  untaggedOnly: boolean
  genres: string[]
  genreMatch: string
  excludeGenres: string[]
  metaTags: string[]
  metaTagMatch: string
  ratingState: string
  ratingRange: [number, number]
  communityRange: [number, number]
  readRange: [number, number]
  monitored: string
  qualityProfile: string
  completeness: string
  sourceState: string
  sources: string[]
  sourceMatch: string
  fileSources: string[]
  fileSourceMatch: string
  rootFolders: string[]
  incognito: string
  muted: string
  overrides: string[]
}

export type LibraryFilterSetters = { [K in keyof LibraryFilterValues]: (value: LibraryFilterValues[K]) => void }

export interface LibraryFilterOptions {
  /** The statuses present, without "All statuses". */
  statuses: FilterOption[]
  types: FilterOption[]
  contentRatings: FilterOption[]
  readingStatuses: FilterOption[]
  credits: FilterOption[]
  publishers: FilterOption[]
  tags: FilterOption[]
  genres: FilterOption[]
  metaTags: FilterOption[]
  sources: FilterOption[]
  fileSources: FilterOption[]
  rootFolders: FilterOption[]
  overrides: FilterOption[]
  /** Starting with Any and Instance default. */
  qualityProfiles: FilterOption[]
  /** Starting with Any. */
  sourceStates: FilterOption[]
  chapterModes: FilterOption[]
  ratingStates: FilterOption[]
  matchModes: FilterOption[]
}

export interface LibraryFilterPanelProps {
  opened: boolean
  onClose: () => void
  values: LibraryFilterValues
  set: LibraryFilterSetters
  options: LibraryFilterOptions
  /** Some series carries a community score, or a saved filter narrowed it anyway. */
  hasCommunityRatings: boolean
  readTracking: boolean
  showRootFolder: boolean
  shownCount: number
  activeCount: number
  onReset: () => void
  /** Closes the drawer and opens the tag manager. */
  onManageTags: () => void
}

const PAGES = [
  'root',
  'myRating',
  'read',
  'contentRating',
  'chapters',
  'year',
  'community',
  'anime',
  'credits',
  'publishers',
  'genres',
  'tags',
  'metaTags',
  'addedWithin',
  'monitored',
  'completeness',
  'qualityProfile',
  'incognito',
  'muted',
  'overrides',
  'rootFolders',
  'sourceState',
  'clash',
  'sources',
  'fileSources',
] as const
type FilterPage = (typeof PAGES)[number]
const PARENTS = Object.fromEntries(PAGES.map((p) => [p, 'root'])) as Record<FilterPage, FilterPage>

/** The long lists render this many matches, as the old dropdowns did; search narrows the rest. */
const LIST_LIMIT = 100
/** Lists longer than this get a search box. */
const SEARCH_FROM = 8
const TOP_GENRES = 8

/** The picked values that are not among the options (a saved filter, a renamed source) stay listed so they can be cleared. */
function withPicked(options: FilterOption[], picked: string[]): FilterOption[] {
  const known = new Set(options.map((o) => o.value))
  const stale = picked.filter((v) => !known.has(v))
  return stale.length ? [...options, ...stale.map((v) => ({ value: v, label: v }))] : options
}

function visibleChoices(options: FilterOption[], picked: string[], query: string): DrillChoice[] {
  const q = query.trim().toLocaleLowerCase()
  const matches = q ? options.filter((o) => o.label.toLocaleLowerCase().includes(q)) : options
  const shown = matches.slice(0, LIST_LIMIT)
  const shownSet = new Set(shown.map((o) => o.value))
  const pickedBeyond = matches.filter((o) => picked.includes(o.value) && !shownSet.has(o.value))
  return [...shown, ...pickedBeyond].map((o) => ({ value: o.value, label: o.label }))
}

function nameOf(options: FilterOption[], value: string) {
  const o = options.find((x) => x.value === value)
  return o ? (o.name ?? o.label) : value
}

/**
 * The Library's filters on the drill grammar: chip rows for the filters people reach for most, then
 * one row per filter that opens its page inside the drawer. Every control writes the page's state
 * straight away, so the grid behind and the footer count follow as you go.
 */
export function LibraryFilterPanel({
  opened,
  onClose,
  values: v,
  set,
  options,
  hasCommunityRatings,
  readTracking,
  showRootFolder,
  shownCount,
  activeCount,
  onReset,
  onManageTags,
}: LibraryFilterPanelProps) {
  const { t } = useLingui()
  const pages = useDrillPages<FilterPage>('root', PARENTS)
  const { page, open, back, backRef } = pages

  const [wasOpened, setWasOpened] = useState(opened)
  if (opened !== wasOpened) {
    setWasOpened(opened)
    if (opened) pages.reset()
  }

  // The root page comes back at the scroll position it was left at, so the row that opened a page is on screen.
  const scrollRef = useRef<HTMLDivElement>(null)
  const rootScroll = useRef(0)
  const lastPage = useRef<FilterPage>(page)
  useLayoutEffect(() => {
    const el = scrollRef.current
    if (!el || lastPage.current === page) return
    el.scrollTop = page === 'root' ? rootScroll.current : 0
    lastPage.current = page
  }, [page])
  const openPage = (to: FilterPage) => {
    rootScroll.current = scrollRef.current?.scrollTop ?? 0
    open(to)
  }

  // Mantine's own Escape is off on a sub-page, so there Escape steps back one page instead of closing.
  useEffect(() => {
    if (!opened || page === 'root') return
    const onKey = (event: KeyboardEvent) => {
      if (event.key !== 'Escape' || event.isComposing) return
      event.preventDefault()
      back()
    }
    window.addEventListener('keydown', onKey, true)
    return () => window.removeEventListener('keydown', onKey, true)
  }, [opened, page, back])

  const any = t`Any`
  const selected = (count: number) => plural(count, { one: '# selected', other: '# selected' })
  const listValue = (opts: FilterOption[], picked: string[]) =>
    picked.length === 0 ? null : picked.length <= 2 ? picked.map((x) => nameOf(opts, x)).join(', ') : selected(picked.length)
  const triValue = (opts: FilterOption[], inc: string[], exc: string[]) => {
    if (inc.length === 0 && exc.length === 0) return null
    if (inc.length + exc.length <= 2) {
      const included = inc.map((x) => nameOf(opts, x)).join(', ')
      const excluded = exc.map((x) => nameOf(opts, x)).join(', ')
      if (exc.length === 0) return included
      if (inc.length === 0) return t`Not ${excluded}`
      return t`${included}, not ${excluded}`
    }
    if (exc.length === 0) return selected(inc.length)
    const inCount = inc.length
    const outCount = exc.length
    return t`${inCount} in, ${outCount} out`
  }
  const boundsValue = (lo: number | null, hi: number | null, fmt: (n: number) => string = formatNumber) => {
    if (lo != null && hi != null) {
      const from = fmt(lo)
      const to = fmt(hi)
      return t`${from} to ${to}`
    }
    if (lo != null) {
      const from = fmt(lo)
      return t`From ${from}`
    }
    if (hi != null) {
      const to = fmt(hi)
      return t`Up to ${to}`
    }
    return null
  }
  const sliderValue = ([lo, hi]: [number, number], min: number, max: number, fmt: (n: number) => string) =>
    lo > min || hi < max ? boundsValue(lo, hi, fmt) : null
  const choiceValue = (opts: FilterOption[], value: string) => (value === 'all' ? null : nameOf(opts, value))

  const percent = (n: number) => formatPercent(n / 100)

  const animeOptions: FilterOption[] = [
    { value: 'all', label: any },
    { value: 'has', label: t`Has anime` },
    { value: 'none', label: t`No anime` },
  ]
  const clashOptions: FilterOption[] = [
    { value: 'all', label: any },
    { value: 'clash', label: t`Numbering clash` },
    { value: 'none', label: t`No numbering clash` },
  ]
  const monitoredOptions: FilterOption[] = [
    { value: 'all', label: any },
    { value: 'monitored', label: t`Monitored` },
    { value: 'unmonitored', label: t`Unmonitored` },
  ]
  const completenessOptions: FilterOption[] = [
    { value: 'all', label: any },
    { value: 'behind', label: t`Behind (missing chapters)` },
    { value: 'complete', label: t`Complete` },
  ]
  const incognitoOptions: FilterOption[] = [
    { value: 'all', label: any },
    { value: 'on', label: t`Incognito` },
    { value: 'off', label: t`Not incognito` },
  ]
  const mutedOptions: FilterOption[] = [
    { value: 'all', label: any },
    { value: 'muted', label: t`Muted` },
    { value: 'unmuted', label: t`Not muted` },
  ]

  const myRatingValue =
    v.ratingState === 'rated'
      ? v.ratingRange[0] > 1 || v.ratingRange[1] < 10
        ? boundsValue(v.ratingRange[0], v.ratingRange[1])
        : nameOf(options.ratingStates, 'rated')
      : choiceValue(options.ratingStates, v.ratingState)
  const { addedWithin } = v
  const tagsValue = v.untaggedOnly ? t`Untagged only` : triValue(options.tags, v.tags, v.excludeTags)

  const row = (target: FilterPage, icon: Parameters<typeof DrillRow>[0]['icon'], label: string, value: ReactNode) => (
    <DrillRow
      ref={pages.rowRef(target)}
      icon={icon}
      label={label}
      value={value ?? any}
      active={value != null}
      onClick={() => openPage(target)}
    />
  )

  const statusChips = withPicked(options.statuses, v.status === 'all' ? [] : [v.status])
  const typeChips = withPicked(options.types, v.types)
  const topGenres = options.genres.slice(0, TOP_GENRES)
  const genreChips = withPicked(
    topGenres,
    v.genres.filter((g) => !topGenres.some((o) => o.value === g)),
  ).map((o) => ({ ...o, label: o.name ?? o.label }))
  const toggle = (list: string[], value: string) => (list.includes(value) ? list.filter((x) => x !== value) : [...list, value])

  const sub = (title: string, children: ReactNode, help?: ReactNode) => (
    <div>
      <DrillBack ref={backRef} title={title} onBack={back} />
      {help && <div className="drill-field-help library-filter-help">{help}</div>}
      {children}
    </div>
  )
  const single = (title: string, opts: FilterOption[], value: string, onPick: (value: string) => void, help?: ReactNode) =>
    sub(
      title,
      <DrillChoices
        label={title}
        value={value}
        options={opts}
        onPick={(picked) => {
          onPick(picked)
          back()
        }}
      />,
      help,
    )
  const matchField = (label: string, mode: string, onChange: (mode: string) => void) => (
    <DrillField icon={IconAdjustmentsHorizontal} title={t`Match`}>
      <SegmentedControl
        aria-label={t`Match mode for ${label}`}
        size="xs"
        fullWidth
        value={mode}
        onChange={onChange}
        data={options.matchModes}
      />
    </DrillField>
  )

  const content = (() => {
    switch (page) {
      case 'root':
        return (
          <div className="library-filter-root">
            {statusChips.length > 0 && (
              <DrillGroup heading={t`Status`}>
                <TagChips className="library-filter-chips">
                  {statusChips.map((o) => (
                    <TagChip key={o.value} active={v.status === o.value} onClick={() => set.status(v.status === o.value ? 'all' : o.value)}>
                      {o.label}
                    </TagChip>
                  ))}
                </TagChips>
              </DrillGroup>
            )}
            <DrillGroup heading={t`Reading status`}>
              <TagChips className="library-filter-chips">
                {withPicked(options.readingStatuses, v.readingStatuses).map((o) => (
                  <TagChip
                    key={o.value}
                    active={v.readingStatuses.includes(o.value)}
                    onClick={() => set.readingStatuses(toggle(v.readingStatuses, o.value))}
                  >
                    {o.label}
                  </TagChip>
                ))}
              </TagChips>
            </DrillGroup>
            {typeChips.length > 0 && (
              <DrillGroup heading={t`Type`}>
                <TagChips className="library-filter-chips">
                  {typeChips.map((o) => (
                    <TagChip key={o.value} active={v.types.includes(o.value)} onClick={() => set.types(toggle(v.types, o.value))}>
                      {o.label}
                    </TagChip>
                  ))}
                </TagChips>
              </DrillGroup>
            )}
            {genreChips.length > 0 && (
              <DrillGroup heading={t`Genres`}>
                <TagChips className="library-filter-chips">
                  {genreChips.map((o) => {
                    const on = v.genres.includes(o.value)
                    return (
                      <TagChip
                        key={o.value}
                        active={on}
                        onClick={() => {
                          set.genres(toggle(v.genres, o.value))
                          if (!on && v.excludeGenres.includes(o.value))
                            set.excludeGenres(v.excludeGenres.filter((g) => g !== o.value))
                        }}
                      >
                        {o.label}
                      </TagChip>
                    )
                  })}
                </TagChips>
              </DrillGroup>
            )}

            <DrillGroup heading={t`Reading`}>
              {row('myRating', IconStar, t`My rating`, myRatingValue)}
              {readTracking && row('read', IconBook, t`Read`, sliderValue(v.readRange, 0, 100, percent))}
            </DrillGroup>
            <DrillGroup heading={t`About the series`}>
              {row('contentRating', IconShield, t`Content rating`, listValue(options.contentRatings, v.contentRatings))}
              {row('chapters', IconFileText, t`Chapters`, boundsValue(v.chapterMin, v.chapterMax))}
              {row('year', IconCalendar, t`Year`, boundsValue(v.yearMin, v.yearMax, String))}
              {hasCommunityRatings &&
                row(
                  'community',
                  IconRanking,
                  t({ message: 'Rating', context: 'MangaBaka score filter' }),
                  sliderValue(v.communityRange, 0, 10, formatDecimal),
                )}
              {row('anime', IconDeviceTv, t`Anime adaptation`, choiceValue(animeOptions, v.anime))}
              {row('credits', IconFeather, t`Author or artist`, listValue(options.credits, v.credits))}
              {row('publishers', IconBooks, t`Publisher`, listValue(options.publishers, v.publishers))}
            </DrillGroup>
            <DrillGroup heading={t`Tags and genres`}>
              {row('genres', IconBookmarks, t`Genres`, triValue(options.genres, v.genres, v.excludeGenres))}
              {row('tags', IconTag, t`Your tags`, tagsValue)}
              {row('metaTags', IconTags, t`Tags`, listValue(options.metaTags, v.metaTags))}
            </DrillGroup>
            <DrillGroup heading={t`Library state`}>
              {row(
                'addedWithin',
                IconClock,
                t`Added within (days)`,
                addedWithin == null ? null : plural(addedWithin, { one: '# day', other: '# days' }),
              )}
              {row('monitored', IconEye, t`Monitoring`, choiceValue(monitoredOptions, v.monitored))}
              {row('completeness', IconChecks, t`Completeness`, choiceValue(completenessOptions, v.completeness))}
              {row('qualityProfile', IconAdjustments, t`Quality profile`, choiceValue(options.qualityProfiles, v.qualityProfile))}
              {row('incognito', IconEyeOff, t`Incognito`, choiceValue(incognitoOptions, v.incognito))}
              {row('muted', IconBell, t`Notifications`, choiceValue(mutedOptions, v.muted))}
              {row('overrides', IconSettings, t`Own settings`, listValue(options.overrides, v.overrides))}
              {showRootFolder && row('rootFolders', IconFolder, t`Root folder`, listValue(options.rootFolders, v.rootFolders))}
            </DrillGroup>
            <DrillGroup heading={t`Files and sources`}>
              {row('sourceState', IconPlugConnected, t`Source state`, choiceValue(options.sourceStates, v.sourceState))}
              {row('clash', IconSortAscendingNumbers, t`Chapter numbering`, choiceValue(clashOptions, v.clash))}
              {row('sources', IconWorld, t`Sources`, listValue(options.sources, v.sources))}
              {row('fileSources', IconDownload, t`Downloaded from`, listValue(options.fileSources, v.fileSources))}
            </DrillGroup>
          </div>
        )
      case 'myRating':
        return sub(
          t`My rating`,
          <DrillField icon={IconStar} title={t`My rating`}>
            <SegmentedControl
              aria-label={t`My rating`}
              size="xs"
              fullWidth
              value={v.ratingState}
              onChange={set.ratingState}
              data={options.ratingStates}
            />
            {v.ratingState === 'rated' && (
              <DrillSliderLines
                min={1}
                max={10}
                step={1}
                value={v.ratingRange}
                onChange={set.ratingRange}
                fromLabel={t`My rating from`}
                toLabel={t`My rating to`}
                format={formatNumber}
              />
            )}
          </DrillField>,
        )
      case 'read':
        return sub(
          t`Read`,
          <DrillSliderRange
            icon={IconBook}
            title={t`Read`}
            help={
              <>
                {t`Share of the series you've read.`} {t`Leave at 0–100% to ignore.`}
              </>
            }
            min={0}
            max={100}
            step={5}
            value={v.readRange}
            onChange={set.readRange}
            fromLabel={t`Read from`}
            toLabel={t`Read to`}
            format={percent}
          />,
        )
      case 'community': {
        const title = t({ message: 'Rating', context: 'MangaBaka score filter' })
        return sub(
          title,
          <DrillSliderRange
            icon={IconRanking}
            title={title}
            help={t`Leave at 0–10 to ignore. Narrowing it hides series without a score.`}
            min={0}
            max={10}
            step={0.5}
            value={v.communityRange}
            onChange={set.communityRange}
            fromLabel={t`Rating from`}
            toLabel={t`Rating to`}
            format={formatDecimal}
          />,
        )
      }
      case 'contentRating':
        return sub(
          t`Content rating`,
          <DrillMultiChoices
            label={t`Content rating`}
            value={v.contentRatings}
            options={withPicked(options.contentRatings, v.contentRatings)}
            onChange={set.contentRatings}
          />,
        )
      case 'chapters':
        return sub(
          t`Chapters`,
          <DrillNumberRange
            icon={IconFileText}
            title={t`Chapters`}
            help={t`Leave both boxes empty to ignore.`}
            from={{ label: t`Minimum chapters`, placeholder: t`Min`, value: v.chapterMin, onChange: set.chapterMin }}
            to={{ label: t`Maximum chapters`, placeholder: t`Max`, value: v.chapterMax, onChange: set.chapterMax }}
            inputProps={{ min: 0, allowDecimal: false }}
          >
            <SegmentedControl
              aria-label={t`Chapters`}
              size="xs"
              fullWidth
              value={v.chapterMode}
              onChange={set.chapterMode}
              data={options.chapterModes}
              mb="xs"
            />
          </DrillNumberRange>,
        )
      case 'year':
        return sub(
          t`Year`,
          <DrillNumberRange
            icon={IconCalendar}
            title={t`Year`}
            help={t`Leave both boxes empty to ignore. Series without a year drop out once one is set.`}
            from={{ label: t`Earliest year`, placeholder: t`From`, value: v.yearMin, onChange: set.yearMin }}
            to={{ label: t`Latest year`, placeholder: t`To`, value: v.yearMax, onChange: set.yearMax }}
            inputProps={{ min: 0, max: 9999, allowDecimal: false, allowNegative: false, thousandSeparator: false }}
          />,
        )
      case 'addedWithin':
        return sub(
          t`Added within (days)`,
          <DrillField icon={IconClock} title={t`Added within (days)`}>
            <NumberInput
              aria-label={t`Added within (days)`}
              className="field-default-value"
              placeholder={t`Any time`}
              min={1}
              allowDecimal={false}
              allowNegative={false}
              value={v.addedWithin ?? ''}
              onChange={(n) => set.addedWithin(n === '' || Number.isNaN(Number(n)) ? null : Number(n))}
            />
          </DrillField>,
        )
      case 'anime':
        return single(t`Anime adaptation`, animeOptions, v.anime, set.anime)
      case 'clash':
        return single(
          t`Chapter numbering`,
          clashOptions,
          v.clash,
          set.clash,
          t`Sources that number the same chapters differently`,
        )
      case 'monitored':
        return single(t`Monitoring`, monitoredOptions, v.monitored, set.monitored)
      case 'completeness':
        return single(t`Completeness`, completenessOptions, v.completeness, set.completeness)
      case 'qualityProfile':
        return single(t`Quality profile`, withPicked(options.qualityProfiles, [v.qualityProfile]), v.qualityProfile, set.qualityProfile)
      case 'incognito':
        return single(t`Incognito`, incognitoOptions, v.incognito, set.incognito)
      case 'muted':
        return single(t`Notifications`, mutedOptions, v.muted, set.muted)
      case 'sourceState':
        return single(
          t`Source state`,
          options.sourceStates,
          v.sourceState,
          set.sourceState,
          t`Counts both switches: the per-series link and the global source toggle`,
        )
      case 'credits':
        return (
          <ListPage title={t`Author or artist`} back={sub} options={options.credits} value={v.credits} onChange={set.credits} />
        )
      case 'publishers':
        return (
          <ListPage title={t`Publisher`} back={sub} options={options.publishers} value={v.publishers} onChange={set.publishers} />
        )
      case 'overrides':
        return (
          <ListPage
            title={t`Own settings`}
            help={t`Series that override the default for this`}
            back={sub}
            options={options.overrides}
            value={v.overrides}
            onChange={set.overrides}
          />
        )
      case 'rootFolders':
        return (
          <ListPage title={t`Root folder`} back={sub} options={options.rootFolders} value={v.rootFolders} onChange={set.rootFolders} />
        )
      case 'metaTags':
        return (
          <ListPage
            title={t`Tags`}
            help={t`From the metadata provider, not your own tags`}
            back={sub}
            options={options.metaTags}
            value={v.metaTags}
            onChange={set.metaTags}
            match={v.metaTags.length > 1 ? matchField(t`Tags`, v.metaTagMatch, set.metaTagMatch) : null}
          />
        )
      case 'sources':
        return (
          <ListPage
            title={t`Sources`}
            help={t`Linked to the series, enabled or not`}
            back={sub}
            options={options.sources}
            value={v.sources}
            onChange={set.sources}
            match={v.sources.length > 1 ? matchField(t`Sources`, v.sourceMatch, set.sourceMatch) : null}
          />
        )
      case 'fileSources':
        return (
          <ListPage
            title={t`Downloaded from`}
            help={t`Where the files on disk came from, which can outlive the link`}
            back={sub}
            options={options.fileSources}
            value={v.fileSources}
            onChange={set.fileSources}
            match={v.fileSources.length > 1 ? matchField(t`Downloaded from`, v.fileSourceMatch, set.fileSourceMatch) : null}
          />
        )
      case 'genres':
        return (
          <ListPage
            title={t`Genres`}
            help={t`Tap once to include, again to leave out, a third time to clear.`}
            back={sub}
            options={options.genres}
            value={v.genres}
            exclude={v.excludeGenres}
            onTriChange={(inc, exc) => {
              set.genres(inc)
              set.excludeGenres(exc)
            }}
            match={v.genres.length > 1 ? matchField(t`Genres`, v.genreMatch, set.genreMatch) : null}
          />
        )
      case 'tags':
        return (
          <ListPage
            title={t`Your tags`}
            help={t`Tap once to include, again to leave out, a third time to clear.`}
            back={sub}
            options={options.tags}
            value={v.tags}
            exclude={v.excludeTags}
            disabled={v.untaggedOnly}
            onTriChange={(inc, exc) => {
              set.tags(inc)
              set.excludeTags(exc)
            }}
            before={
              <DrillSwitch
                label={t`Untagged only`}
                hint={t`Series with none of your tags`}
                checked={v.untaggedOnly}
                onChange={set.untaggedOnly}
              />
            }
            match={!v.untaggedOnly && v.tags.length > 1 ? matchField(t`Your tags`, v.tagMatch, set.tagMatch) : null}
            after={<DrillRow icon={IconSettings} label={t`Manage tags`} chevron={false} onClick={onManageTags} />}
          />
        )
    }
  })()

  return (
    <Drawer
      opened={opened}
      onClose={onClose}
      position="right"
      size="sm"
      title={t`Filters`}
      closeOnEscape={page === 'root'}
      classNames={{ content: 'library-filter-drawer', body: 'library-filter-drawer-body' }}
    >
      <div className="library-filter-frame">
        <div ref={scrollRef} className="library-filter-scroll">
          <DrillPanes page={page} depth={pages.depth} bleed={4} easeHeight={false}>
            {content}
          </DrillPanes>
        </div>
        <DrillFooter
          onReset={onReset}
          resetDisabled={activeCount === 0}
          status={<Plural value={shownCount} one="# series" other="# series" />}
          onDone={onClose}
        />
      </div>
    </Drawer>
  )
}

/** A page of many choices: an optional switch and Match field, a search box once the list is long, then the list. */
function ListPage({
  title,
  help,
  back,
  options,
  value,
  onChange,
  exclude,
  onTriChange,
  disabled,
  before,
  match,
  after,
}: {
  title: string
  help?: ReactNode
  back: (title: string, children: ReactNode, help?: ReactNode) => ReactNode
  options: FilterOption[]
  value: string[]
  onChange?: (value: string[]) => void
  exclude?: string[]
  onTriChange?: (include: string[], exclude: string[]) => void
  disabled?: boolean
  before?: ReactNode
  match?: ReactNode
  after?: ReactNode
}) {
  const { t } = useLingui()
  const [query, setQuery] = useState('')
  const picked = [...value, ...(exclude ?? [])]
  const all = withPicked(options, picked)
  const choices = visibleChoices(all, picked, query)
  const list =
    all.length === 0 ? (
      <div className="drill-field-help library-filter-help">{t`None available`}</div>
    ) : choices.length === 0 ? (
      <div className="drill-field-help library-filter-help">{t`No matches`}</div>
    ) : onTriChange ? (
      <DrillTriChoices
        label={title}
        include={value}
        exclude={exclude ?? []}
        options={choices}
        onChange={onTriChange}
        disabled={disabled}
      />
    ) : (
      <DrillMultiChoices label={title} value={value} options={choices} onChange={onChange ?? (() => {})} disabled={disabled} />
    )
  return back(
    title,
    <>
      {before}
      {match}
      {all.length > SEARCH_FROM && <DrillSearch label={t`Search ${title}`} placeholder={t`Search`} value={query} onChange={setQuery} />}
      {list}
      {after}
    </>,
    help,
  )
}
