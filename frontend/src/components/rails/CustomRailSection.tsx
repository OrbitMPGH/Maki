import { useEffect, useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { Trans, useLingui } from '@lingui/react/macro'
import { t as now } from '@lingui/core/macro'
import {
  ActionIcon,
  Button,
  Group,
  Menu,
  Modal,
  SimpleGrid,
  Skeleton,
  Stack,
  Text,
} from '@mantine/core'
import { useIntersection } from '@mantine/hooks'
import { notifications } from '@mantine/notifications'
import {
  IconChevronRight,
  IconCopy,
  IconDots,
  IconFilter,
  IconLibrary,
  IconPencil,
  IconPlus,
  IconSparkles,
  IconTrash,
} from '@tabler/icons-react'
import {
  useSeries,
  useSeriesIdLookup,
  type DiscoverRail,
  type RecommendationApplyState,
  type RecommendationItem,
} from '../../api/hooks'
import {
  customRailAsDiscoverRail,
  useCustomRailItems,
  useDeleteCustomRail,
  type CustomRail,
  type CustomRailPlacement,
} from '../../api/customRails'
import { useReadTracking } from '../../api/reader'
import type { SeriesDto } from '../../api/types'
import { filtersFromSpec } from '../CatalogueFilters'
import { CoverCard } from '../ui/CoverCard'
import { DiscoverRailRow, EngineRailRow } from '../ui/DiscoverRail'
import { SectionHeader } from '../ui/SectionHeader'
import { useDensityPref } from '../ui/viewPrefs'
import { CustomRailEditor, type CustomRailDraft } from './CustomRailEditor'

const ICONS = { library: IconLibrary, recommendations: IconSparkles, catalogue: IconFilter }

/**
 * One custom rail, fetched once it nears the viewport: every rail on a page is its own query, and a
 * recommendation rail can cost a full index scan when its pool is cold.
 *
 * @param onShowMoreCatalogue Discover's own expand view. Without it (on Home), a catalogue rail's
 *   "Show more" goes to Discover and opens it there.
 */
export function CustomRailSection({
  rail,
  limit,
  onOpen,
  onShowMoreCatalogue,
}: {
  rail: CustomRail
  limit: number
  onOpen: (item: RecommendationItem) => void
  onShowMoreCatalogue?: (rail: DiscoverRail) => void
}) {
  const { t } = useLingui()
  const navigate = useNavigate()
  const seriesIdFor = useSeriesIdLookup()
  const { ref, entry } = useIntersection({ rootMargin: '600px' })
  const [seen, setSeen] = useState(false)
  useEffect(() => {
    if (entry?.isIntersecting) setSeen(true)
  }, [entry?.isIntersecting])

  const { data, isLoading } = useCustomRailItems(rail.id, limit, seen)
  const [editing, setEditing] = useState<{ rail?: CustomRail; draft?: CustomRailDraft } | null>(null)
  const [confirmDelete, setConfirmDelete] = useState(false)
  const [libraryOpen, setLibraryOpen] = useState(false)
  const remove = useDeleteCustomRail()

  const source = rail.spec.source
  const count = source === 'library' ? data?.seriesIds.length : data?.items.length
  const empty = data != null && !data.unavailable && count === 0
  const otherPlacement: CustomRailPlacement = rail.placement === 'home' ? 'discover' : 'home'
  const canDuplicateAcross = source !== 'library' || otherPlacement === 'home'

  const showMore = () => {
    if (source === 'recommendations') {
      navigate('/discover/recommended', {
        state: {
          recommendationFilters: filtersFromSpec(rail.spec.filters ?? {}),
          seeds: rail.spec.seeds ?? undefined,
          source: 'custom-rail',
          obscurity: rail.spec.obscurity ?? 0,
          diversity: rail.spec.diversity ?? 0,
        } satisfies RecommendationApplyState,
      })
    } else if (source === 'catalogue') {
      if (onShowMoreCatalogue) onShowMoreCatalogue(customRailAsDiscoverRail(rail, data?.items ?? []))
      else navigate('/discover', { state: { expandRailId: rail.id } })
    } else {
      setLibraryOpen(true)
    }
  }

  const railName = rail.name
  const duplicate = (placement: CustomRailPlacement) =>
    setEditing({ draft: { name: rail.name, placement, spec: rail.spec } })

  return (
    <div ref={ref}>
      <SectionHeader
        icon={ICONS[source]}
        title={rail.name}
        count={count || undefined}
        action={
          <Group gap={4} wrap="nowrap">
            {!empty && !data?.unavailable && (
              <Button
                variant="subtle"
                size="xs"
                rightSection={<IconChevronRight size={14} />}
                onClick={showMore}
              >
                <Trans>Show more</Trans>
              </Button>
            )}
            <Menu position="bottom-end" withinPortal shadow="md">
              <Menu.Target>
                <ActionIcon variant="subtle" color="var(--neutral)" aria-label={t`Rail options`}>
                  <IconDots size={16} />
                </ActionIcon>
              </Menu.Target>
              <Menu.Dropdown>
                <Menu.Item leftSection={<IconPencil size={14} />} onClick={() => setEditing({ rail })}>
                  <Trans>Edit</Trans>
                </Menu.Item>
                <Menu.Item leftSection={<IconCopy size={14} />} onClick={() => duplicate(rail.placement)}>
                  <Trans>Duplicate</Trans>
                </Menu.Item>
                {canDuplicateAcross && (
                  <Menu.Item leftSection={<IconCopy size={14} />} onClick={() => duplicate(otherPlacement)}>
                    {otherPlacement === 'home' ? <Trans>Copy to Home</Trans> : <Trans>Copy to Discover</Trans>}
                  </Menu.Item>
                )}
                <Menu.Divider />
                <Menu.Item
                  color="var(--danger)"
                  leftSection={<IconTrash size={14} />}
                  onClick={() => setConfirmDelete(true)}
                >
                  <Trans>Delete</Trans>
                </Menu.Item>
              </Menu.Dropdown>
            </Menu>
          </Group>
        }
      />

      {data?.unavailable ? (
        <Text c="var(--ink-3)" size="sm">
          <Trans>This rail needs the local MangaBaka database, which is switched off or still downloading.</Trans>
        </Text>
      ) : empty ? (
        <Text c="var(--ink-3)" size="sm">
          <Trans>Nothing matches this rail right now.</Trans>
        </Text>
      ) : !data || isLoading ? (
        <RailPlaceholder engine={source === 'recommendations'} />
      ) : source === 'library' ? (
        <LibraryRailRow ids={data.seriesIds} />
      ) : source === 'recommendations' ? (
        <EngineRailRow items={data.items} seriesIdFor={seriesIdFor} onOpen={onOpen} />
      ) : (
        <DiscoverRailRow items={data.items} seriesIdFor={seriesIdFor} onOpen={onOpen} />
      )}

      {editing && <CustomRailEditor {...editing} onClose={() => setEditing(null)} />}
      {libraryOpen && <LibraryRailModal rail={rail} onClose={() => setLibraryOpen(false)} />}
      <Modal opened={confirmDelete} onClose={() => setConfirmDelete(false)} title={t`Delete rail`} size="sm">
        <Stack gap="md">
          <Text size="sm">
            <Trans>Delete the rail "{railName}"? Its filters go with it.</Trans>
          </Text>
          <Group justify="flex-end">
            <Button variant="subtle" onClick={() => setConfirmDelete(false)}>
              <Trans>Cancel</Trans>
            </Button>
            <Button
              color="var(--danger)"
              loading={remove.isPending}
              onClick={() =>
                remove.mutate(rail.id, {
                  onSuccess: () => {
                    setConfirmDelete(false)
                    notifications.show({ color: 'var(--ok)', message: now`Rail deleted` })
                  },
                  onError: (err) => notifications.show({ color: 'var(--danger)', message: String(err) }),
                })
              }
            >
              <Trans>Delete</Trans>
            </Button>
          </Group>
        </Stack>
      </Modal>
    </div>
  )
}

function RailPlaceholder({ engine }: { engine: boolean }) {
  return (
    <div className="discover-rail" data-engine={engine || undefined} aria-hidden>
      {Array.from({ length: 8 }, (_, i) => (
        <div key={i} className="discover-rail-item">
          <Skeleton radius="lg" style={{ aspectRatio: '2 / 3' }} />
        </div>
      ))}
    </div>
  )
}

function useSeriesById() {
  const { data: series } = useSeries()
  return useMemo(() => new Map((series ?? []).map((s) => [s.id, s])), [series])
}

const noop = () => {}

/** A library rail's series, drawn with the library grid's own card. */
function LibraryRailRow({ ids }: { ids: number[] }) {
  const byId = useSeriesById()
  const readTracking = useReadTracking()
  const series = ids.map((id) => byId.get(id)).filter((s): s is SeriesDto => s != null)
  return (
    <div className="discover-rail">
      {series.map((s) => (
        <div key={s.id} className="discover-rail-item">
          <CoverCard series={s} selectMode={false} selected={false} readTracking={readTracking} onToggle={noop} />
        </div>
      ))}
    </div>
  )
}

/** Every series a library rail matches, as a grid. */
function LibraryRailModal({ rail, onClose }: { rail: CustomRail; onClose: () => void }) {
  const { data } = useCustomRailItems(rail.id, 500)
  const byId = useSeriesById()
  const readTracking = useReadTracking()
  const { cols } = useDensityPref('custom-rail-expand')
  const series = (data?.seriesIds ?? []).map((id) => byId.get(id)).filter((s): s is SeriesDto => s != null)
  return (
    <Modal opened onClose={onClose} fullScreen title={rail.name}>
      <SimpleGrid cols={cols} spacing="md">
        {series.map((s) => (
          <CoverCard key={s.id} series={s} selectMode={false} selected={false} readTracking={readTracking} onToggle={noop} />
        ))}
      </SimpleGrid>
    </Modal>
  )
}

/** Opens the editor for a new rail. */
export function AddRailButton({
  placement,
  label,
  onCreated,
}: {
  placement: CustomRailPlacement
  label?: React.ReactNode
  onCreated?: (rail: CustomRail) => void
}) {
  const [open, setOpen] = useState(false)
  const draft: CustomRailDraft = {
    placement,
    spec: { source: placement === 'home' ? 'library' : 'recommendations' },
  }
  return (
    <>
      <Button variant="subtle" size="xs" leftSection={<IconPlus size={14} />} onClick={() => setOpen(true)}>
        {label ?? <Trans>Add a rail</Trans>}
      </Button>
      {open && <CustomRailEditor draft={draft} onSaved={onCreated} onClose={() => setOpen(false)} />}
    </>
  )
}
