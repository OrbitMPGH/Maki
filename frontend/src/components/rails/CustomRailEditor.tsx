import { errorText } from '../../api/errorText'
import { useMemo, useState } from 'react'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { t as now } from '@lingui/core/macro'
import {
  Button,
  Group,
  Modal,
  Pill,
  SegmentedControl,
  Select,
  SimpleGrid,
  Stack,
  Switch,
  Text,
  TextInput,
} from '@mantine/core'
import { useDebouncedValue } from '@mantine/hooks'
import { notifications } from '../../lib/toast'
import { useMetadataSettings, useSavedFilters, type RecommendationSeed } from '../../api/hooks'
import {
  RAIL_SORTS,
  RAIL_SORT_LABELS,
  RAIL_SOURCE_LABELS,
  useCreateCustomRail,
  useCustomRailCount,
  useUpdateCustomRail,
  type CustomRail,
  type CustomRailPlacement,
  type CustomRailSort,
  type CustomRailSource,
  type CustomRailSpec,
} from '../../api/customRails'
import { useLabel } from '../../i18n-context'
import { CatalogueFilters, filtersFromSpec, useCatalogueFilters } from '../CatalogueFilters'
import { RecommenderDials } from '../discover/RecommenderDials'
import { ModalFooter, ModalHeading } from '../ui/ModalShell'
import { IconPlus } from '../../lib/icons'

/** A rail that doesn't exist yet: what the editor opens with when creating one. */
export interface CustomRailDraft {
  name?: string
  placement: CustomRailPlacement
  spec: CustomRailSpec
}

const SOURCES: CustomRailSource[] = ['library', 'recommendations', 'catalogue']

/**
 * Creates or edits one custom rail in a modal. Pass `rail` to edit it, or `draft` to create a new
 * one from whatever the caller already had on screen. Mount it only while open, so its state
 * starts fresh.
 */
export function CustomRailEditor({
  rail,
  draft,
  onSaved,
  onClose,
}: {
  rail?: CustomRail
  draft?: CustomRailDraft
  onSaved?: (rail: CustomRail) => void
  onClose: () => void
}) {
  const { t } = useLingui()
  return (
    <Modal
      opened
      onClose={onClose}
      size="xl"
      title={
        <ModalHeading
          title={rail ? t`Edit rail` : t`New rail`}
          description={<Trans>A shelf of titles you define once and pin to Home or Discover.</Trans>}
        />
      }
    >
      <CustomRailForm
        inModal
        rail={rail}
        draft={draft}
        onSaved={(saved) => {
          onSaved?.(saved)
          onClose()
        }}
        onCancel={onClose}
      />
    </Modal>
  )
}

/**
 * The rail's fields and its save button, without a frame, so the layout editor can show them in
 * its own side panel. Saving writes the rail straight away.
 *
 * @param placementLocked Hides the Home/Discover choice, for a caller that already decided.
 */
export function CustomRailForm({
  rail,
  draft,
  onSaved,
  onCancel,
  placementLocked = false,
  inModal = false,
}: {
  rail?: CustomRail
  draft?: CustomRailDraft
  onSaved?: (rail: CustomRail) => void
  onCancel?: () => void
  placementLocked?: boolean
  /** Ends the form with the modal's pinned footer instead of an inline action row. */
  inModal?: boolean
}) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const { data: metadata } = useMetadataSettings()
  const discoverAvailable = Boolean(metadata?.dumpPresent)
  const create = useCreateCustomRail()
  const update = useUpdateCustomRail()

  const start = rail ?? draft ?? { name: '', placement: 'home' as const, spec: { source: 'library' as const } }
  const [name, setName] = useState(start.name ?? '')
  const [placement, setPlacement] = useState<CustomRailPlacement>(start.placement)
  const [source, setSource] = useState<CustomRailSource>(start.spec.source)
  const [sort, setSort] = useState<CustomRailSort | null>(start.spec.sort ?? RAIL_SORTS[start.spec.source][0] ?? null)
  const [excludeOwned, setExcludeOwned] = useState(start.spec.excludeOwned ?? true)
  const [seeds, setSeeds] = useState<RecommendationSeed[]>(start.spec.seeds ?? [])
  const [obscurity, setObscurity] = useState(start.spec.obscurity ?? 0)
  const [diversity, setDiversity] = useState(start.spec.diversity ?? 0)
  const [libraryFilterId, setLibraryFilterId] = useState<number | null>(start.spec.libraryFilterId ?? null)
  const { data: savedFilters } = useSavedFilters()
  // A rail whose filter was deleted falls back to the filters below; saving drops the dead reference.
  const savedFilterGone =
    source === 'library' && libraryFilterId != null && savedFilters != null && !savedFilters.some((f) => f.id === libraryFilterId)
  const liveFilterId = savedFilterGone ? null : libraryFilterId
  const [error, setError] = useState<string | null>(null)
  const catalogue = useCatalogueFilters(filtersFromSpec(start.spec.filters ?? {}))

  const changeSource = (next: CustomRailSource) => {
    setSource(next)
    if (!sort || !RAIL_SORTS[next].includes(sort)) setSort(RAIL_SORTS[next][0] ?? null)
  }

  const changePlacement = (next: CustomRailPlacement) => {
    setPlacement(next)
    // Discover is about what the reader doesn't own, so a library rail can't go there.
    if (next === 'discover' && source === 'library') changeSource('recommendations')
  }

  const spec: CustomRailSpec = {
    source,
    filters: catalogue.build(),
    sort: sort && RAIL_SORTS[source].includes(sort) ? sort : null,
    excludeOwned: source === 'catalogue' ? excludeOwned : false,
    seeds: source === 'recommendations' ? seeds : null,
    obscurity: source === 'recommendations' ? obscurity : 0,
    diversity: source === 'recommendations' ? diversity : 0,
    libraryFilterId: source === 'library' ? liveFilterId : null,
  }
  const fromSavedFilter = source === 'library' && liveFilterId != null
  // Saved filter names are typed by you, so they pass through untranslated.
  const savedFilterOptions = [
    { value: '', label: t`None, use the filters below` },
    ...(savedFilters ?? []).map((f) => ({ value: String(f.id), label: f.name })),
  ]
  const specJson = JSON.stringify(spec)
  const [debouncedJson] = useDebouncedValue(specJson, 400)
  const countSpec = useMemo(() => JSON.parse(debouncedJson) as CustomRailSpec, [debouncedJson])
  const { data: count } = useCustomRailCount(
    source === 'library' || (source !== 'recommendations' && discoverAvailable) ? countSpec : null,
  )

  const sourceOptions = SOURCES.map((value) => ({
    value,
    label: renderLabel(RAIL_SOURCE_LABELS[value]),
    disabled:
      (value === 'library' && placement === 'discover') || (value !== 'library' && !discoverAvailable),
  }))
  const sortOptions = RAIL_SORTS[source].map((value) => ({ value, label: renderLabel(RAIL_SORT_LABELS[value]) }))

  const pending = create.isPending || update.isPending
  const save = () => {
    const trimmed = name.trim()
    if (!trimmed) {
      setError(t`Give it a name`)
      return
    }
    const body = { name: trimmed, placement, spec }
    const options = {
      onSuccess: (saved: CustomRail) => {
        notifications.show({ color: 'var(--ok)', message: rail ? now`Rail saved` : now`Rail added` })
        onSaved?.(saved)
      },
      onError: (err: unknown) => setError(errorText(err)),
    }
    if (rail) update.mutate({ id: rail.id, ...body }, options)
    else create.mutate(body, options)
  }

  const matchCount = count != null && <Plural value={count} one="# title matches" other="# titles match" />
  const cancelButton = onCancel && (
    <Button variant="default" onClick={onCancel}>
      <Trans>Cancel</Trans>
    </Button>
  )
  const saveButton = (
    <Button loading={pending} onClick={save} leftSection={rail ? undefined : <IconPlus size={16} />}>
      {rail ? <Trans>Save</Trans> : <Trans>Add rail</Trans>}
    </Button>
  )

  return (
      <Stack gap="md">
        <TextInput
          label={t`Name`}
          placeholder={t`Teen romcoms`}
          value={name}
          error={error}
          data-autofocus
          onChange={(e) => {
            setName(e.currentTarget.value)
            setError(null)
          }}
        />

        <SimpleGrid cols={{ base: 1, sm: placementLocked ? 1 : 2 }} spacing="md">
          {!placementLocked && (
          <Stack gap={4}>
            <Text size="sm" fw={500}>
              <Trans>Show on</Trans>
            </Text>
            <SegmentedControl
              aria-label={t`Show on`}
              value={placement}
              onChange={(v) => changePlacement(v as CustomRailPlacement)}
              data={[
                { value: 'home', label: t`Home` },
                { value: 'discover', label: t`Discover`, disabled: !discoverAvailable },
              ]}
            />
          </Stack>
          )}
          <Stack gap={4}>
            <Text size="sm" fw={500}>
              <Trans>Titles from</Trans>
            </Text>
            <SegmentedControl
              aria-label={t`Titles from`}
              value={source}
              onChange={(v) => changeSource(v as CustomRailSource)}
              data={sourceOptions}
            />
          </Stack>
        </SimpleGrid>

        {source === 'library' && (savedFilters ?? []).length > 0 && (
          <Select
            label={t`Saved Library filter`}
            data={savedFilterOptions}
            value={liveFilterId != null ? String(liveFilterId) : ''}
            onChange={(v) => setLibraryFilterId(v ? Number(v) : null)}
            allowDeselect={false}
            comboboxProps={{ withinPortal: true }}
          />
        )}
        {savedFilterGone && (
          <Text size="sm" c="var(--warn)">
            <Trans>The saved Library filter this rail used was deleted. Pick another, or use the filters below.</Trans>
          </Text>
        )}

        <Text size="xs" c="var(--ink-3)">
          {fromSavedFilter ? (
            <Trans>Series your saved Library filter shows, in its own sort. Editing the filter changes this rail.</Trans>
          ) : source === 'library' ? (
            <Trans>Series you already have that match these filters.</Trans>
          ) : source === 'recommendations' ? (
            <Trans>Picks for you, ranked the same way as the Recommended tab with these settings.</Trans>
          ) : (
            <Trans>Anything in the MangaBaka catalogue that matches these filters.</Trans>
          )}
        </Text>

        {!fromSavedFilter && <CatalogueFilters controls={catalogue.controls} cols={{ base: 1, sm: 2 }} />}

        {source === 'recommendations' && (
          <>
            {seeds.length > 0 && (
              <Stack gap={4}>
                <Text size="sm" fw={500}>
                  <Trans>Seeded from</Trans>
                </Text>
                <Pill.Group>
                  {seeds.map((seed) => (
                    <Pill
                      key={seed.id}
                      withRemoveButton
                      onRemove={() => setSeeds((prev) => prev.filter((s) => s.id !== seed.id))}
                    >
                      {seed.title ?? `#${seed.id}`}
                    </Pill>
                  ))}
                </Pill.Group>
              </Stack>
            )}
            <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="lg">
              <RecommenderDials
                obscurity={obscurity}
                setObscurity={setObscurity}
                diversity={diversity}
                setDiversity={setDiversity}
              />
            </SimpleGrid>
          </>
        )}

        {!fromSavedFilter && (sortOptions.length > 0 || source === 'catalogue') && (
          <Group gap="lg" align="flex-end">
            {sortOptions.length > 0 && (
              <Select
                label={t`Sort by`}
                w={200}
                data={sortOptions}
                value={sort}
                onChange={(v) => setSort((v as CustomRailSort) ?? RAIL_SORTS[source][0] ?? null)}
                allowDeselect={false}
              />
            )}
            {source === 'catalogue' && (
              <Switch
                label={t`Leave out series in your library`}
                checked={excludeOwned}
                onChange={(e) => setExcludeOwned(e.currentTarget.checked)}
                mb={8}
              />
            )}
          </Group>
        )}

        {inModal ? (
          <ModalFooter status={matchCount}>
            {cancelButton}
            {saveButton}
          </ModalFooter>
        ) : (
          <Group justify="space-between">
            <Text size="sm" c="var(--ink-3)">
              {matchCount}
            </Text>
            <Group gap="xs">
              {cancelButton}
              {saveButton}
            </Group>
          </Group>
        )}
      </Stack>
  )
}
