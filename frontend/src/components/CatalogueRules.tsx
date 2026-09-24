import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import {
  ActionIcon,
  Button,
  Checkbox,
  Combobox,
  Group,
  Loader,
  Pill,
  PillsInput,
  Popover,
  SegmentedControl,
  Select,
  Stack,
  Text,
  Tooltip,
  useCombobox,
} from '@mantine/core'
import { useDebouncedValue } from '@mantine/hooks'
import { IconEyeOff, IconListTree, IconPlus, IconSitemap, IconTarget, IconTrash } from '@tabler/icons-react'
import { usePageState } from '../lib/pageState'
import {
  useDiscoverCount,
  useHiddenContent,
  useRecommendationTags,
  useSaveHiddenContent,
  type CatalogueRule,
  type CatalogueTerm,
  type RecommendationFilters,
  type RuleMode,
  type TagOption,
} from '../api/hooks'
import { useGenreOptions } from './CatalogueFilters'
import { TagBrowserModal } from './TagBrowserModal'
import { rankTagMatches } from '../lib/tagTree'

type GenreState = 'include' | 'exclude'

/**
 * Everything the genre and tag half of a filter panel holds. The simple view and the rules view
 * are two editors over one filter: the simple fields compile to at most three rules, and a rule
 * set that fits that shape reads back into them. `view` only says which editor is showing.
 */
export interface TermFilterState {
  view: 'simple' | 'rules'
  genres: Record<string, GenreState>
  tagsIn: CatalogueTerm[]
  tagMode: 'all' | 'any'
  tagsOut: CatalogueTerm[]
  rules: CatalogueRule[]
}

const EMPTY: TermFilterState = {
  view: 'simple',
  genres: {},
  tagsIn: [],
  tagMode: 'all',
  tagsOut: [],
  rules: [],
}

const genre = (name: string): CatalogueTerm => ({ kind: 'genre', name })
const tag = (name: string): CatalogueTerm => ({ kind: 'tag', name })

function compileSimple(s: TermFilterState): CatalogueRule[] {
  const rules: CatalogueRule[] = []
  const genresIn = Object.keys(s.genres).filter((g) => s.genres[g] === 'include')
  const genresOut = Object.keys(s.genres).filter((g) => s.genres[g] === 'exclude')
  if (genresIn.length) rules.push({ mode: 'all', terms: genresIn.map(genre) })
  if (s.tagsIn.length) rules.push({ mode: s.tagMode, terms: s.tagsIn })
  if (genresOut.length || s.tagsOut.length) {
    rules.push({ mode: 'none', terms: [...genresOut.map(genre), ...s.tagsOut] })
  }
  return rules
}

/**
 * The simple fields a rule set reads back into, or null when it needs the rules view: more than
 * one rule of a kind, a genre in an "any" rule, or genres and tags required together in one rule.
 */
function toSimple(rules: CatalogueRule[]): Omit<TermFilterState, 'view' | 'rules'> | null {
  const out = { genres: {} as Record<string, GenreState>, tagsIn: [] as CatalogueTerm[], tagMode: 'all' as 'all' | 'any', tagsOut: [] as CatalogueTerm[] }
  let genreRule = false
  let tagRule = false
  let noneRule = false
  for (const rule of rules) {
    if (rule.terms.length === 0) continue
    const kinds = new Set(rule.terms.map((term) => term.kind))
    if (rule.mode === 'none') {
      if (noneRule) return null
      noneRule = true
      for (const term of rule.terms) {
        if (term.kind === 'genre') out.genres[term.name] = 'exclude'
        else out.tagsOut.push(term)
      }
    } else if (kinds.size === 1 && kinds.has('genre') && rule.mode === 'all') {
      if (genreRule) return null
      genreRule = true
      for (const term of rule.terms) out.genres[term.name] = 'include'
    } else if (kinds.size === 1 && kinds.has('tag')) {
      if (tagRule) return null
      tagRule = true
      out.tagsIn = rule.terms
      out.tagMode = rule.mode
    } else {
      return null
    }
  }
  return out
}

/** Reads a stored or carried filter, legacy `genres`/`tags` lists included, into panel state. */
function fromFilters(f: RecommendationFilters | undefined | null): TermFilterState {
  if (!f) return EMPTY
  const rules: CatalogueRule[] = []
  if (f.genres?.length) rules.push({ mode: 'all', terms: f.genres.map(genre) })
  if (f.tags?.length) rules.push({ mode: 'all', terms: f.tags.map(tag) })
  rules.push(...(f.rules ?? []))
  const simple = toSimple(rules)
  return simple ? { ...EMPTY, ...simple, view: 'simple' } : { ...EMPTY, view: 'rules', rules }
}

function liveRules(s: TermFilterState): CatalogueRule[] {
  const rules = s.view === 'simple' ? compileSimple(s) : s.rules
  return rules.filter((rule) => rule.terms.length > 0)
}

/**
 * The genre and tag half of a filter panel. Kept apart from the sliders so the Recommended tab,
 * which owns its other controls itself, can use it too.
 *
 * @param key `usePageState` key, or null for a panel with nothing to remember.
 */
export function useTermFilters(key: string | null, initial?: RecommendationFilters) {
  const [state, setState] = usePageState<TermFilterState>(key, () => fromFilters(initial))

  const rules = liveRules(state)
  const isCustomized = rules.length > 0

  const build = (): Pick<RecommendationFilters, 'rules'> => (rules.length ? { rules } : {})

  const reset = useCallback(() => setState(EMPTY), [setState])
  const hydrate = useCallback((f: RecommendationFilters) => setState(fromFilters(f)), [setState])

  return { state, setState, rules, isCustomized, build, reset, hydrate }
}

export type TermFilterControls = Pick<ReturnType<typeof useTermFilters>, 'state' | 'setState'>

/** Localized mode names, shared by the rule rows and the summary. */
function useModeLabels(): Record<RuleMode, string> {
  const { t } = useLingui()
  return { all: t`Has all of`, any: t`Has any of`, none: t`Has none of` }
}

/** The genre and tag editor: tri-state chips and two tag pickers, or the rule builder. */
export function TermFilters({ controls }: { controls: TermFilterControls }) {
  const { t } = useLingui()
  const { state, setState } = controls
  const simpleShape = state.view === 'rules' ? toSimple(state.rules) : null
  const canSimplify = state.view === 'simple' || simpleShape != null

  const switchView = (view: string) => {
    if (view === state.view) return
    if (view === 'rules') {
      setState({ ...state, view: 'rules', rules: compileSimple(state) })
    } else if (simpleShape) {
      setState({ ...EMPTY, ...simpleShape, view: 'simple' })
    }
  }

  return (
    <Stack gap="sm">
      <Group justify="space-between" wrap="nowrap">
        <Text size="sm" fw={500}>
          <Trans>Genres and tags</Trans>
        </Text>
        <Tooltip
          label={t`These rules need the rules view. Remove some to switch back.`}
          disabled={canSimplify}
          withArrow
        >
          <SegmentedControl
            size="xs"
            value={state.view}
            onChange={switchView}
            data={[
              { value: 'simple', label: t`Simple`, disabled: !canSimplify },
              { value: 'rules', label: t`Rules` },
            ]}
          />
        </Tooltip>
      </Group>
      {state.view === 'simple' ? (
        <SimpleTerms state={state} setState={setState} />
      ) : (
        <RuleBuilder rules={state.rules} onChange={(rules) => setState({ ...state, rules })} />
      )}
    </Stack>
  )
}

function SimpleTerms({ state, setState }: TermFilterControls) {
  const { t } = useLingui()
  const curated = useGenreOptions()
  // A carried filter (the taste page, an old default) can name a genre outside the curated list.
  // It still has to be visible, or it would narrow the results with no chip to clear it.
  const genreOptions = useMemo(() => {
    const known = new Set(curated.map((o) => o.value.toLowerCase()))
    const extra = Object.keys(state.genres)
      .filter((name) => !known.has(name.toLowerCase()))
      .map((name) => ({ value: name, label: name }))
    return [...curated, ...extra]
  }, [curated, state.genres])

  const cycle = (name: string) => {
    const next = { ...state.genres }
    const current = next[name]
    if (current === undefined) next[name] = 'include'
    else if (current === 'include') next[name] = 'exclude'
    else delete next[name]
    setState({ ...state, genres: next })
  }

  return (
    <Stack gap="sm">
      <div>
        <Text size="xs" c="var(--ink-3)" mb={6}>
          <Trans>Click a genre once to require it, twice to exclude it.</Trans>
        </Text>
        <div className="tag-chips">
          {genreOptions.map(({ value, label }) => {
            const mode = state.genres[value]
            return (
              <button
                key={value}
                type="button"
                className="tag-chip term-chip"
                data-interactive
                data-state={mode}
                aria-pressed={mode ? true : false}
                aria-label={
                  mode === 'include'
                    ? t`${label}: required`
                    : mode === 'exclude'
                      ? t`${label}: excluded`
                      : label
                }
                onClick={() => cycle(value)}
              >
                <span className="tag-chip-body">
                  {mode === 'include' ? '+ ' : mode === 'exclude' ? '− ' : ''}
                  {label}
                </span>
              </button>
            )
          })}
        </div>
      </div>
      <TermPicker
        label={
          <Group gap="xs" wrap="nowrap">
            <span>
              <Trans>Tags to include</Trans>
            </span>
            <SegmentedControl
              size="xs"
              value={state.tagMode}
              onChange={(v) => setState({ ...state, tagMode: v as 'all' | 'any' })}
              data={[
                { value: 'all', label: t`All` },
                { value: 'any', label: t`Any` },
              ]}
            />
          </Group>
        }
        kinds={['tag']}
        tone="include"
        value={state.tagsIn}
        onChange={(tagsIn) => setState({ ...state, tagsIn })}
      />
      <TermPicker
        label={<Trans>Tags to exclude</Trans>}
        kinds={['tag']}
        tone="exclude"
        value={state.tagsOut}
        onChange={(tagsOut) => setState({ ...state, tagsOut })}
      />
    </Stack>
  )
}

function RuleBuilder({
  rules,
  onChange,
}: {
  rules: CatalogueRule[]
  onChange: (rules: CatalogueRule[]) => void
}) {
  const { t } = useLingui()
  const modes = useModeLabels()
  const update = (i: number, rule: CatalogueRule) =>
    onChange(rules.map((r, j) => (j === i ? rule : r)))

  return (
    <Stack gap="xs">
      {rules.length === 0 && (
        <Text size="sm" c="var(--ink-3)">
          <Trans>No rules yet. Add one to require or rule out genres and tags. A series has to pass every rule.</Trans>
        </Text>
      )}
      {rules.map((rule, i) => (
        <Group key={i} gap="xs" align="flex-start" wrap="nowrap" className="rule-row">
          <Select
            size="sm"
            w={140}
            allowDeselect={false}
            value={rule.mode}
            onChange={(v) => update(i, { ...rule, mode: (v as RuleMode) ?? 'all' })}
            data={(['all', 'any', 'none'] as const).map((value) => ({ value, label: modes[value] }))}
            aria-label={t`Rule type`}
          />
          <div style={{ flex: 1, minWidth: 0 }}>
            <TermPicker
              kinds={['genre', 'tag']}
              tone={rule.mode === 'none' ? 'exclude' : 'include'}
              value={rule.terms}
              onChange={(terms) => update(i, { ...rule, terms })}
            />
          </div>
          <ActionIcon
            variant="subtle"
            color="var(--neutral)"
            size="lg"
            aria-label={t`Remove rule`}
            onClick={() => onChange(rules.filter((_, j) => j !== i))}
          >
            <IconTrash size={16} />
          </ActionIcon>
        </Group>
      ))}
      <Group>
        <Button
          size="xs"
          variant="default"
          leftSection={<IconPlus size={14} />}
          onClick={() => onChange([...rules, { mode: rules.length === 0 ? 'all' : 'any', terms: [] }])}
        >
          <Trans>Add rule</Trans>
        </Button>
      </Group>
    </Stack>
  )
}

const OPTION_LIMIT = 60

/**
 * A searchable picker over genres and tags. Each tag is listed with its place in the tag tree,
 * because the names alone mislead ("Adult" is a sexual-content intensity, not an age). A picked
 * tag opens its options on click: subtags, central only, and hiding it everywhere.
 */
export function TermPicker({
  label,
  kinds,
  tone,
  value,
  onChange,
  placeholder,
  allowHide = true,
}: {
  label?: ReactNode
  kinds: ('genre' | 'tag')[]
  tone: 'include' | 'exclude'
  value: CatalogueTerm[]
  onChange: (terms: CatalogueTerm[]) => void
  placeholder?: string
  allowHide?: boolean
}) {
  const { t } = useLingui()
  const combobox = useCombobox({ onDropdownClose: () => combobox.resetSelectedOption() })
  const [search, setSearch] = useState('')
  const { data: tagOptions, isLoading } = useRecommendationTags()
  const genreOptions = useGenreOptions()
  const tagInfo = useTagInfo()

  const picked = useMemo(
    () => new Set(value.map((term) => `${term.kind}:${term.name.toLowerCase()}`)),
    [value],
  )

  const needle = search.trim().toLowerCase()
  const [tagNeedle] = useDebouncedValue(needle, 100)
  const genres = kinds.includes('genre')
    ? genreOptions.filter(
        (g) => !picked.has(`genre:${g.value.toLowerCase()}`) && (!needle || g.label.toLowerCase().includes(needle)),
      )
    : []
  const tags = useMemo(() => {
    if (!kinds.includes('tag')) return []
    const skip = (option: TagOption) => picked.has(`tag:${option.name.toLowerCase()}`)
    if (tagNeedle) return rankTagMatches(tagOptions ?? [], tagNeedle, OPTION_LIMIT, skip).items
    const out: TagOption[] = []
    for (const option of tagOptions ?? []) {
      if (skip(option)) continue
      out.push(option)
      if (out.length >= OPTION_LIMIT) break
    }
    return out
  }, [kinds, tagOptions, picked, tagNeedle])
  const [browsing, setBrowsing] = useState(false)
  const canBrowse = kinds.includes('tag')

  const add = (term: CatalogueTerm) => {
    onChange([...value, term])
    setSearch('')
  }

  const onSubmit = (key: string) => {
    const [kind, ...rest] = key.split(':')
    add({ kind: kind as 'genre' | 'tag', name: rest.join(':') })
  }

  const noTags = kinds.includes('tag') && !isLoading && (tagOptions?.length ?? 0) === 0

  // Enter takes the best match without an arrow press first, which is what typing a name expects.
  useEffect(() => {
    if (tagNeedle) combobox.selectFirstOption()
    // `combobox` is a fresh object each render; only a new search should move the selection.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tagNeedle])

  return (
    <>
      <Combobox store={combobox} onOptionSubmit={onSubmit} withinPortal>
        <Combobox.DropdownTarget>
          <PillsInput
            label={label}
            onClick={() => combobox.openDropdown()}
            size="sm"
            rightSectionPointerEvents="all"
            rightSection={
              canBrowse ? (
                <Tooltip label={t`Browse tags`} withArrow openDelay={300}>
                  <ActionIcon
                    variant="subtle"
                    color="var(--neutral)"
                    aria-label={t`Browse tags`}
                    // The input would take focus and reopen its dropdown under the modal.
                    onMouseDown={(e) => {
                      e.preventDefault()
                      e.stopPropagation()
                    }}
                    onClick={(e) => {
                      e.stopPropagation()
                      combobox.closeDropdown()
                      setBrowsing(true)
                    }}
                  >
                    <IconListTree size={16} />
                  </ActionIcon>
                </Tooltip>
              ) : undefined
            }
          >
            <Pill.Group>
              {value.map((term, i) => (
                <TermPill
                  key={`${term.kind}:${term.name}`}
                  term={term}
                  tone={tone}
                  info={tagInfo.get(term.name.toLowerCase())}
                  allowHide={allowHide}
                  onOpenOptions={() => combobox.closeDropdown()}
                  onChange={(next) => onChange(value.map((v, j) => (j === i ? next : v)))}
                  onRemove={() => onChange(value.filter((_, j) => j !== i))}
                />
              ))}
              <Combobox.EventsTarget>
                <PillsInput.Field
                  value={search}
                  placeholder={value.length ? undefined : (placeholder ?? (kinds.includes('genre') ? t`Search genres and tags` : t`Search tags`))}
                  onFocus={() => combobox.openDropdown()}
                  onBlur={() => combobox.closeDropdown()}
                  onChange={(e) => {
                    combobox.openDropdown()
                    combobox.updateSelectedOptionIndex()
                    setSearch(e.currentTarget.value)
                  }}
                  onKeyDown={(e) => {
                    if (e.key === 'Backspace' && search.length === 0 && value.length > 0) {
                      e.preventDefault()
                      onChange(value.slice(0, -1))
                    }
                  }}
                />
              </Combobox.EventsTarget>
            </Pill.Group>
          </PillsInput>
        </Combobox.DropdownTarget>

        <Combobox.Dropdown>
          <Combobox.Options mah={300} style={{ overflowY: 'auto' }}>
            {genres.length > 0 && (
              <Combobox.Group label={t`Genres`}>
                {genres.map((g) => (
                  <Combobox.Option value={`genre:${g.value}`} key={g.value}>
                    {g.label}
                  </Combobox.Option>
                ))}
              </Combobox.Group>
            )}
            {tags.length > 0 && (
              <Combobox.Group label={kinds.includes('genre') ? t`Tags` : undefined}>
                {tags.map((option) => (
                  <Combobox.Option value={`tag:${option.name}`} key={option.name}>
                    <Group justify="space-between" gap="xs" wrap="nowrap">
                      <div style={{ minWidth: 0 }}>
                        <Text size="sm" truncate>
                          {option.name}
                        </Text>
                        {option.path && (
                          <Text size="xs" c="var(--ink-3)" truncate>
                            {option.path}
                          </Text>
                        )}
                      </div>
                      <Text size="xs" c="var(--ink-4)">
                        {option.count.toLocaleString()}
                      </Text>
                    </Group>
                  </Combobox.Option>
                ))}
              </Combobox.Group>
            )}
            {isLoading && kinds.includes('tag') && (
              <Combobox.Empty>
                <Loader size="xs" />
              </Combobox.Empty>
            )}
            {!isLoading && genres.length === 0 && tags.length === 0 && (
              <Combobox.Empty>
                {noTags && !kinds.includes('genre')
                  ? t`Tags appear once the recommendation index is built`
                  : t`No matches`}
              </Combobox.Empty>
            )}
          </Combobox.Options>
        </Combobox.Dropdown>
      </Combobox>
      {canBrowse && (
        <TagBrowserModal
          opened={browsing}
          onClose={() => setBrowsing(false)}
          kinds={kinds}
          tone={tone}
          value={value}
          onChange={onChange}
        />
      )}
    </>
  )
}

/** Tag name (lower-cased) to its option, for pills that need the path and subtag flag. */
function useTagInfo() {
  const { data } = useRecommendationTags()
  return useMemo(() => new Map((data ?? []).map((option) => [option.name.toLowerCase(), option])), [data])
}

function TermPill({
  term,
  tone,
  info,
  allowHide,
  onOpenOptions,
  onChange,
  onRemove,
}: {
  term: CatalogueTerm
  tone: 'include' | 'exclude'
  info?: TagOption
  allowHide: boolean
  onOpenOptions: () => void
  onChange: (term: CatalogueTerm) => void
  onRemove: () => void
}) {
  const { t } = useLingui()
  const [opened, setOpened] = useState(false)
  const renderGenre = useGenreLabel()
  const hideEverywhere = useHideEverywhere()
  const name = term.kind === 'genre' ? renderGenre(term.name) : term.name

  const pill = (
    <Pill
      withRemoveButton
      onRemove={onRemove}
      className="term-pill"
      data-state={tone}
      // The input around the pill focuses its field and opens the picker's dropdown on click;
      // a tag's options would open on top of it.
      onMouseDown={
        term.kind === 'tag'
          ? (e) => {
              e.preventDefault()
              e.stopPropagation()
            }
          : undefined
      }
      onClick={
        term.kind === 'tag'
          ? (e) => {
              e.stopPropagation()
              onOpenOptions()
              setOpened((o) => !o)
            }
          : undefined
      }
      style={term.kind === 'tag' ? { cursor: 'pointer' } : undefined}
      title={info?.path ? `${info.path} > ${term.name}` : undefined}
    >
      {term.subtags && <IconSitemap size={11} style={{ marginRight: 3, verticalAlign: -1 }} aria-label={t`Includes subtags`} />}
      {term.central && <IconTarget size={11} style={{ marginRight: 3, verticalAlign: -1 }} aria-label={t`Central only`} />}
      {name}
    </Pill>
  )

  if (term.kind === 'genre') return pill

  return (
    <Popover opened={opened} onChange={setOpened} position="bottom-start" withArrow shadow="md" width={260}>
      <Popover.Target>{pill}</Popover.Target>
      <Popover.Dropdown>
        <Stack gap="xs">
          {info?.path && (
            <Text size="xs" c="var(--ink-3)">
              {info.path}
            </Text>
          )}
          <Checkbox
            size="xs"
            label={t`Include subtags`}
            description={info?.hasSubtags ? t`Also match every tag filed under this one` : t`This tag has no subtags`}
            disabled={!info?.hasSubtags && !term.subtags}
            checked={!!term.subtags}
            onChange={(e) => onChange({ ...term, subtags: e.currentTarget.checked })}
          />
          <Checkbox
            size="xs"
            label={t`Only where it is central`}
            description={t`Skip series where it is only a passing element`}
            checked={!!term.central}
            onChange={(e) => onChange({ ...term, central: e.currentTarget.checked })}
          />
          {allowHide && (
            <Button
              size="xs"
              variant="subtle"
              color="var(--danger)"
              leftSection={<IconEyeOff size={14} />}
              loading={hideEverywhere.isPending}
              onClick={() => {
                hideEverywhere.hide({ kind: 'tag', name: term.name })
                setOpened(false)
              }}
            >
              <Trans>Hide everywhere on Discover</Trans>
            </Button>
          )}
        </Stack>
      </Popover.Dropdown>
    </Popover>
  )
}

function useGenreLabel() {
  const options = useGenreOptions()
  return useCallback(
    (name: string) => options.find((o) => o.value.toLowerCase() === name.toLowerCase())?.label ?? name,
    [options],
  )
}

/** Appends one term to the never-show list. */
export function useHideEverywhere() {
  const { data } = useHiddenContent()
  const save = useSaveHiddenContent()
  return {
    isPending: save.isPending,
    hide: (term: CatalogueTerm) => {
      const current = data?.terms ?? []
      if (current.some((c) => c.kind === term.kind && c.name.toLowerCase() === term.name.toLowerCase())) return
      save.mutate({ terms: [...current, term] })
    },
  }
}

/**
 * Short labels for active rules, for the collapsed-panel summaries and the Recommended tab's chips.
 * Genres render translated; tags stay as MangaBaka names, which is all there is for them.
 */
export function useRuleChips() {
  const { t } = useLingui()
  const renderGenre = useGenreLabel()
  return useCallback(
    (rules: CatalogueRule[]) =>
      rules.map((rule) => {
        const names = rule.terms
          .map((term) => (term.kind === 'genre' ? renderGenre(term.name) : term.name))
          .join(rule.mode === 'any' ? ' / ' : ', ')
        return rule.mode === 'none' ? t`not ${names}` : names
      }),
    [renderGenre, t],
  )
}

/**
 * The live "N series match" line. Debounced on the filters' JSON, since the panel's `build()` is a
 * fresh object every render and a slider drag would otherwise be a request per pixel.
 */
export function FilterMatchCount({
  filters,
  feed = 'Popular',
  genre,
}: {
  filters: RecommendationFilters
  feed?: string
  genre?: string | null
}) {
  const key = JSON.stringify(filters)
  const [debounced] = useDebouncedValue(key, 400)
  const request = useMemo(
    () => ({ feed, genre: genre ?? null, filters: JSON.parse(debounced) as RecommendationFilters }),
    [feed, genre, debounced],
  )
  const { data, isFetching } = useDiscoverCount(request)
  const count = data?.count

  if (count == null) {
    return isFetching ? <Loader size="xs" /> : null
  }
  return (
    <Text size="sm" c="var(--ink-3)" style={{ opacity: isFetching ? 0.6 : 1 }}>
      <Plural value={count} one="# series matches" other="# series match" />
    </Text>
  )
}
