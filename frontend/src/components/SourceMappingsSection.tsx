import { useEffect, useMemo, useState } from 'react'
import {
  ActionIcon,
  Alert,
  Anchor,
  Badge,
  Box,
  Button,
  Card,
  Checkbox,
  Group,
  Image,
  Loader,
  Modal,
  MultiSelect,
  NumberInput,
  Select,
  Skeleton,
  Stack,
  Switch,
  Table,
  Text,
  TextInput,
  Title,
  Tooltip,
  VisuallyHidden,
} from '@mantine/core'
import {
  IconCheck,
  IconColumns,
  IconRuler,
  IconExternalLink,
  IconLink,
  IconPlugConnected,
  IconPower,
  IconRefresh,
  IconTrash,
  IconWand,
} from '@tabler/icons-react'
import { useDebouncedValue } from '@mantine/hooks'
import { notifications } from '@mantine/notifications'
import {
  useAutoMatchSources,
  useCreateMapping,
  useDeleteMapping,
  useRefreshSourceSnapshots,
  useRemoveMapping,
  useResolveSourceUrl,
  useSourceMappings,
  useSourceMatchProgress,
  useSources,
  useSourceSearch,
  useReorderMappings,
  useUpdateMapping,
  type SourceOrderMode,
} from '../api/hooks'
import { MeasureProgress, MeasureResult, ScoutCell } from './SourceMeasurePanel'
import type { SourceMappingDto } from '../api/types'
import {
  QUALITY_TIER_COLOR,
  QUALITY_TIER_LABELS,
  useMeasureSources,
  useSetSourceOrderMode,
  useSourceOrder,
  type QualityTierName,
  type SourceQualityDto,
} from '../api/upgrades'
import { useAuth } from '../auth/AuthProvider'
import { formatDateTime } from '../format'
import { SourceCompareModal } from './SourceCompareModal'
import { Trans, useLingui } from '@lingui/react/macro'
import { useLingui as useLinguiReact } from '@lingui/react'
import { msg, t as now, plural } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { useLabel } from '../i18n-context'
import { SOURCE_ICONS } from '../sourceIcons'
import { useSourceLabel } from '../sourceLabels'

const ORIGIN_LABELS: Record<string, MessageDescriptor> = {
  TitleSearch: msg`Title search`,
  CrossId: msg`Cross-id`,
  Manual: msg`Manual`,
}

const ORIGIN_COLORS: Record<string, string> = {
  TitleSearch: 'var(--info)',
  CrossId: 'var(--watched)',
  Manual: 'var(--ok)',
  Unknown: 'var(--neutral)'
}

export function SourceMappingsSection({
  seriesId,
  seriesTitle,
  matching = false,
}: {
  seriesId: number
  seriesTitle: string
  /**
   * Auto-matching is still running in the background (a series added seconds ago). Without it a
   * fresh series reads "No sources linked. Chapters cannot be synced or downloaded." for the half
   * minute the source searches take, which is alarming and wrong.
   */
  matching?: boolean
}) {
  const { data: mappings } = useSourceMappings(seriesId)
  const { data: sourceOrder } = useSourceOrder(seriesId)
  const setOrderMode = useSetSourceOrderMode(seriesId)
  const measureSources = useMeasureSources(seriesId)
  const scout = sourceOrder?.scout
  const scouting = scout?.running ?? false
  const scoutPlanned = scout?.probes ?? 0
  const scoutMeasured = scout?.measured ?? 0
  const reorderMappings = useReorderMappings()
  const seenKey = `maki:scout-seen:${seriesId}`
  const [seenScout, setSeenScout] = useState<string | null>(() => {
    try {
      return localStorage.getItem(seenKey)
    } catch {
      return null
    }
  })
  const dismissScout = () => {
    const finished = scout?.finishedAtUtc ?? null
    setSeenScout(finished)
    try {
      if (finished) localStorage.setItem(seenKey, finished)
    } catch {
      // Private mode or blocked storage: the card just comes back on the next visit.
    }
  }
  const showScoutResult = !!scout && !scout.running && !!scout.finishedAtUtc && seenScout !== scout.finishedAtUtc
  const scoutProgressFor = (mappingId: number) =>
    scouting || showScoutResult ? scout?.sources.find((s) => s.mappingId === mappingId) : undefined
  const qualities = sourceOrder?.sources
  const byQuality = sourceOrder?.mode === 'quality'
  const orderedMappings = useMemo(() => {
    if (!mappings || !byQuality) return mappings
    const rank = new Map(sourceOrder.order.map((id, i) => [id, i]))
    return [...mappings].sort((a, b) => (rank.get(a.id) ?? Infinity) - (rank.get(b.id) ?? Infinity))
  }, [mappings, byQuality, sourceOrder])
  const { data: sources } = useSources()
  const { data: progress } = useSourceMatchProgress(seriesId)
  const updateMapping = useUpdateMapping()
  const deleteMapping = useDeleteMapping()
  const removeMapping = useRemoveMapping()
  const createMapping = useCreateMapping()
  const autoMatch = useAutoMatchSources()
  const refreshSnapshots = useRefreshSourceSnapshots()
  const { can } = useAuth()

  const { t } = useLingui()
  const renderLabel = useLabel()
  const sourceLabel = useSourceLabel()
  const [modalOpen, setModalOpen] = useState(false)
  const [compareOpen, setCompareOpen] = useState(false)
  const [sourceName, setSourceName] = useState<string | null>(null)
  const [query, setQuery] = useState(seriesTitle)
  const [removing, setRemoving] = useState<SourceMappingDto | null>(null)
  const removingSourceName = removing ? sourceLabel(removing.sourceName) : undefined
  const [deleteFiles, setDeleteFiles] = useState(false)
  const [fallbackOpen, setFallbackOpen] = useState(false)
  const [debounced] = useDebouncedValue(query, 400)
  // A pasted URL bypasses search: the backend maps it to a source + series id.
  const pastedUrl = /^https?:\/\//i.test(debounced.trim()) ? debounced.trim() : ''
  const { data: results, isFetching } = useSourceSearch(
    sourceName ?? '',
    pastedUrl ? '' : debounced,
  )
  const {
    data: resolved,
    isFetching: resolving,
    error: resolveError,
  } = useResolveSourceUrl(pastedUrl)

  // A source switched off in Settings can't be linked, and its existing mappings here are
  // inert: their own on/off state is kept and shown read-only rather than being rewritten.
  const unmappedSources = sources?.filter(
    (s) => s.enabled && !mappings?.some((m) => m.sourceName === s.name),
  )
  const sourceDisabled = (name: string) =>
    sources?.some((s) => s.name === name && !s.enabled) ?? false
  const nothingLeftToMatch = !unmappedSources || unmappedSources.length === 0

  // Sources whose row has finished fading out. Kept here rather than derived, because "the exit
  // animation has played" is a fact about this table and nothing else knows it.
  const [faded, setFaded] = useState<Record<string, true>>({})
  // Cleared off the back of the progress map emptying, which `sourceMatchFinished` does. Hanging it
  // off `matching` instead would race the pushes: a run's `Searching` lines land before the client
  // has noticed the series went back to matching.
  useEffect(() => {
    if (Object.keys(progress ?? {}).length === 0) setFaded({})
  }, [progress])

  /**
   * The rows to draw for sources the matcher is still working through — the server announces every
   * source it is about to search before it searches any of them, in priority order, so this is that
   * list minus the ones that have since resolved. Deliberately not backfilled from the source list:
   * a client whose hub connection missed the announcements shows the plain spinner it always did,
   * rather than skeletons for sources that may already be done.
   */
  const pendingRows = useMemo(() => {
    if (!matching) return []
    const linked = new Set(mappings?.map((m) => m.sourceName) ?? [])
    return Object.entries(progress ?? {})
      .filter(([name]) => !linked.has(name) && !faded[name])
      .map(([name, state]) => ({ name, state }))
  }, [matching, mappings, progress, faded])
  // Both switches, as everywhere else: a mapping is only live if its own toggle is on *and* the
  // source isn't switched off globally. Comparing one source against nothing proves nothing.
  const comparable =
    mappings?.filter((m) => m.enabled && !sourceDisabled(m.sourceName)).length ?? 0
  const missingSnapshots =
    mappings?.filter(
      (m) =>
        m.id !== removing?.id &&
        m.enabled &&
        !sourceDisabled(m.sourceName) &&
        !m.chapterSnapshotAt,
    ) ?? []
  const missingSnapshotNames = missingSnapshots.map((m) => sourceLabel(m.sourceName)).join(', ')

  const link = (mappingName: string, sourceSeriesId: string, url: string) =>
    createMapping.mutate(
      { seriesId, sourceName: mappingName, sourceSeriesId, url },
      {
        onSuccess: () => {
          const name = sourceLabel(mappingName)
          notifications.show({ message: now`Linked ${name}`, color: 'var(--ok)' })
          setModalOpen(false)
        },
      },
    )

  return (
    <>
      <Group justify="space-between">
        <Group gap={8}>
          <IconPlugConnected size={18} style={{ opacity: 0.7 }} />
          <Title order={4}><Trans>Sources</Trans></Title>
        </Group>
        {/* Both held while auto-matching runs: (SeriesId, SourceName) is unique, so a hand-linked
            source that the matcher is about to add itself fails its whole batch of mappings. */}
        <Group gap="xs">
          {sourceOrder && (
            <Tooltip
              label={t`Which source a chapter downloads from first. Best quality ranks sources by this series' quality profile and what their recent chapters measured, with priority breaking ties.`}
              withArrow
              multiline
              w={280}
            >
              <Select
                size="xs"
                w={210}
                aria-label={t`Download order`}
                allowDeselect={false}
                value={sourceOrder.seriesMode ?? 'default'}
                onChange={(value) =>
                  value && setOrderMode.mutate(value === 'default' ? null : (value as SourceOrderMode))
                }
                data={[
                  {
                    value: 'default',
                    label:
                      sourceOrder.defaultMode === 'quality'
                        ? t`Default (best quality first)`
                        : t`Default (manual priority)`,
                  },
                  { value: 'manual', label: t`Manual priority` },
                  { value: 'quality', label: t`Best quality first` },
                ]}
              />
            </Tooltip>
          )}
          <Tooltip
            label={
              matching
                ? t`Auto-matching is already running.`
                : nothingLeftToMatch
                  ? t`Every enabled source is already linked.`
                  : t`Search the remaining sources for this title again. Sources already linked are left alone.`
            }
            withArrow
            multiline
            w={240}
          >
            <Box component="span" display="inline-flex">
              <Button
                size="xs"
                variant="default"
                leftSection={<IconWand size={14} />}
                disabled={matching || nothingLeftToMatch}
                loading={autoMatch.isPending}
                onClick={() =>
                  autoMatch.mutate([seriesId], {
                    onSuccess: () =>
                      notifications.show({
                        message: now`Searching sources for a match…`,
                      }),
                  })
                }
              >
                <Trans>Auto-match</Trans>
              </Button>
            </Box>
          </Tooltip>
          <Tooltip
            label={
              matching
                ? t`Auto-matching is still running. It'll be free in a moment.`
                : comparable < 2
                  ? t`Needs at least two enabled sources to compare.`
                  : t`Fetch a sample of the same chapter from each source and rank them by scan quality.`
            }
            withArrow
            multiline
            w={240}
          >
            <Box component="span" display="inline-flex">
              <Button
                size="xs"
                variant="default"
                leftSection={<IconColumns size={14} />}
                disabled={matching || comparable < 2}
                onClick={() => setCompareOpen(true)}
              >
                <Trans>Compare</Trans>
              </Button>
            </Box>
          </Tooltip>
          <Tooltip
            label={
              matching
                ? t`Auto-matching is still running. It'll be free in a moment.`
                : scout && !scout.running
                  ? t`Sample a few pages from three chapters on every enabled source and update each source's quality. Last run measured ${scoutMeasured} of ${scoutPlanned} samples.`
                  : t`Sample a few pages from three chapters on every enabled source and update each source's quality.`
            }
            withArrow
            multiline
            w={260}
          >
            <Box component="span" display="inline-flex">
              <Button
                size="xs"
                variant="default"
                leftSection={scouting ? <Loader size={12} /> : <IconRuler size={14} />}
                disabled={matching || scouting || (mappings?.length ?? 0) === 0}
                loading={measureSources.isPending}
                onClick={() => measureSources.mutate()}
              >
                {scouting ? <Trans>Measuring…</Trans> : <Trans>Measure</Trans>}
              </Button>
            </Box>
          </Tooltip>
          <Tooltip
            label={t`Auto-matching is still running. It'll be free in a moment.`}
            withArrow
            disabled={!matching}
          >
            <Box component="span" display="inline-flex">
              <Button
                size="xs"
                variant="light"
                leftSection={<IconLink size={14} />}
                disabled={matching || nothingLeftToMatch}
                onClick={() => {
                  setSourceName(unmappedSources?.[0]?.name ?? null)
                  setQuery(seriesTitle)
                  setModalOpen(true)
                }}
              >
                <Trans>Link source</Trans>
              </Button>
            </Box>
          </Tooltip>
        </Group>
      </Group>

      {matching && (
        <Group gap="xs">
          <Loader size="xs" />
          <Text c="var(--ink-3)" size="sm">
            {mappings && mappings.length > 0 ? (
              <Trans>Matching the remaining sources…</Trans>
            ) : (
              <Trans>Searching sources for a match…</Trans>
            )}
          </Text>
        </Group>
      )}

      {sourceOrder && scouting && <MeasureProgress order={sourceOrder} />}
      {sourceOrder && showScoutResult && (
        <MeasureResult
          order={sourceOrder}
          label={(id) => sourceLabel(mappings?.find((m) => m.id === id)?.sourceName ?? '')}
          needsFlareSolverr={(id) => {
            const name = mappings?.find((m) => m.id === id)?.sourceName
            return sources?.find((s) => s.name === name)?.needsFlareSolverr ?? false
          }}
          busy={setOrderMode.isPending || reorderMappings.isPending}
          onUseQuality={() => setOrderMode.mutate('quality', { onSuccess: dismissScout })}
          onReorder={() => {
            const rest = sourceOrder.order.filter((id) => !sourceOrder.qualityOrder.includes(id))
            reorderMappings.mutate(
              { seriesId, orderedMappingIds: [...sourceOrder.qualityOrder, ...rest] },
              { onSuccess: dismissScout },
            )
          }}
          onDismiss={dismissScout}
        />
      )}

      {(mappings?.length ?? 0) === 0 && pendingRows.length === 0 ? (
        !matching && (
          <Text c="var(--ink-3)" size="sm">
            <Trans>No sources linked. Chapters cannot be synced or downloaded.</Trans>
          </Text>
        )
      ) : (
        // A persistent scrollbar, since a hover-to-reveal one gave no hint that Enabled/Refreshed were off the visible edge.
        <Table.ScrollContainer
          minWidth={720}
          scrollAreaProps={{ type: 'always', scrollbars: 'x', offsetScrollbars: 'present' }}
        >
          <Table className="ops-table">
            <Table.Thead>
            <Table.Tr>
              <Table.Th><Trans>Source</Trans></Table.Th>
              <Table.Th><Trans>Series</Trans></Table.Th>
              <Table.Th><Trans>Languages</Trans></Table.Th>
              <Table.Th style={{ whiteSpace: 'nowrap' }}><Trans>Priority</Trans></Table.Th>
              <Table.Th style={{ whiteSpace: 'nowrap' }}><Trans>Quality</Trans></Table.Th>
              {/* Icon-only: the label survives for assistive tech via VisuallyHidden. */}
              <Table.Th w={44}>
                <VisuallyHidden><Trans>Enabled</Trans></VisuallyHidden>
                <IconPower size={14} style={{ opacity: 0.7 }} aria-hidden />
              </Table.Th>
              <Table.Th style={{ whiteSpace: 'nowrap' }}><Trans>Refreshed</Trans></Table.Th>
              <Table.Th />
            </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {(orderedMappings ?? []).map((m) => {
              const { sourceName: sourceKey } = m
              const sourceName = sourceLabel(sourceKey)
              return (
              <Table.Tr key={m.id}>
                <Table.Td>
                  <Group gap="xs" wrap="nowrap">
                    {SOURCE_ICONS[m.sourceName] && (
                        <Image
                            src={SOURCE_ICONS[m.sourceName]}
                            alt=""
                            w={20}
                            h={20}
                            fit="contain"
                            style={{
                              flex: '0 0 auto',
                              opacity: sourceDisabled(m.sourceName) ? 0.45 : 1,
                            }}
                        />
                    )}
                    <Text fw={600} size="sm" c={sourceDisabled(m.sourceName) ? 'var(--ink-3)' : undefined}>
                      {sourceName}
                    </Text>
                    {sourceDisabled(m.sourceName) && (
                      <Badge size="xs" color="var(--neutral)" variant="light">
                        <Trans>Source off</Trans>
                      </Badge>
                    )}
                    {m.origin && m.origin != "Unknown" && <Tooltip label={m.origin == "CrossId" ? t`Resolved using ID. High accuracy` : m.origin == "TitleSearch" ? t`Resolved using fuzzy title search. Medium accuracy` : t`Added manually`}>
                      <Badge size="xs" color={ORIGIN_COLORS[m.origin] ?? 'var(--neutral)'} variant="light">
                        {renderLabel(ORIGIN_LABELS[m.origin] ?? m.origin)}
                      </Badge>
                    </Tooltip>}
                  </Group>
                </Table.Td>
                <Table.Td>
                  <Anchor href={m.url} target="_blank" size="sm" ff="monospace" className="mono-id" title={m.sourceSeriesId}>
                    {m.sourceSeriesId}
                  </Anchor>
                </Table.Td>
                <Table.Td>
                  <MappingLanguages
                    mapping={m}
                    supported={
                      sources?.find((s) => s.name === m.sourceName)?.supportsLanguageFilter ?? false
                    }
                    onChange={(languageFilter) => updateMapping.mutate({ ...m, languageFilter })}
                  />
                </Table.Td>
                <Table.Td>
                  <Tooltip
                    label={
                      byQuality
                        ? t`Downloads try the best-scoring source first. Priority only breaks ties.`
                        : t`Lower number = tried first when downloading`
                    }
                    withArrow
                  >
                    <NumberInput
                      size="xs"
                      w={70}
                      min={1}
                      max={99}
                      value={m.priority}
                      onChange={(v) => {
                        const priority = typeof v === 'number' ? v : Number(v)
                        if (Number.isFinite(priority) && priority !== m.priority) {
                          updateMapping.mutate({ ...m, priority })
                        }
                      }}
                    />
                  </Tooltip>
                </Table.Td>
                <Table.Td>
                  {(() => {
                    const progress = scoutProgressFor(m.id)
                    return progress && progress.state !== 'done' ? (
                      <ScoutCell
                        progress={progress}
                        needsFlareSolverr={sources?.find((s) => s.name === m.sourceName)?.needsFlareSolverr ?? false}
                      />
                    ) : (
                      <MappingQuality
                        quality={qualities?.find((q) => q.mappingId === m.id)}
                        tier={sourceOrder?.tiers[m.id]}
                      />
                    )
                  })()}
                </Table.Td>
                <Table.Td>
                  <Tooltip
                    label={t`${sourceName} is switched off in Settings → Sources. This series' setting is kept and applies again once it's back on.`}
                    withArrow
                    multiline
                    w={260}
                    disabled={!sourceDisabled(m.sourceName)}
                  >
                    {/* A disabled input fires no pointer events, so the tooltip has to hang off
                        a wrapper. Greyed, not flipped: the stored flag is left exactly as-is. */}
                    <Box component="span" display="inline-flex">
                      <Switch
                        size="xs"
                        aria-label={t`${sourceName} enabled`}
                        checked={m.enabled}
                        disabled={sourceDisabled(m.sourceName)}
                        onChange={(e) =>
                          updateMapping.mutate({ ...m, enabled: e.currentTarget.checked })
                        }
                      />
                    </Box>
                  </Tooltip>
                </Table.Td>
                <Table.Td>
                  {m.lastError ? (
                    <Tooltip label={m.lastError} withArrow>
                      <Badge size="sm" color="var(--danger)" variant="light">
                        <Trans>Error</Trans>
                      </Badge>
                    </Tooltip>
                  ) : (
                    <Text size="xs" c="var(--ink-3)">
                      {m.lastRefresh ? formatDateTime(m.lastRefresh) : <Trans>never</Trans>}
                    </Text>
                  )}
                </Table.Td>
                <Table.Td>
                  <ActionIcon
                    variant="subtle"
                    color="var(--danger)"
                    onClick={() => {
                      setRemoving(m)
                      setDeleteFiles(false)
                    }}
                    aria-label={t`Remove mapping`}
                  >
                    <IconTrash size={16} />
                  </ActionIcon>
                </Table.Td>
              </Table.Tr>
              )
              })}

              {/* Sources the matcher is still working through. These carry no mapping id — the rows
                  are written in one go when the run ends, and `sourceMatchFinished` is what swaps
                  each of these for the real thing. */}
              {pendingRows.map(({ name, state }) => (
                <Table.Tr
                  key={`pending-${name}`}
                  className={
                    state === 'NoMatch'
                      ? 'source-row-leaving'
                      : state === 'Matched'
                        ? 'source-row-found'
                        : undefined
                  }
                  onAnimationEnd={(e) => {
                    // Animation events bubble, so check which one ended: a Skeleton's shimmer
                    // reaching its end would otherwise retire the row before it had faded.
                    if (state !== 'NoMatch' || !e.animationName.startsWith('source-row-leaving')) {
                      return
                    }
                    setFaded((prev) => ({ ...prev, [name]: true }))
                  }}
                >
                  <Table.Td>
                    <Group gap="xs" wrap="nowrap">
                      {SOURCE_ICONS[name] && (
                        <Image
                          src={SOURCE_ICONS[name]}
                          alt=""
                          w={20}
                          h={20}
                          fit="contain"
                          style={{ flex: '0 0 auto' }}
                        />
                      )}
                      <Text fw={600} size="sm">
                        {sourceLabel(name)}
                      </Text>
                      {state === 'Matched' ? (
                        <Badge
                          size="xs"
                          color="var(--ok)"
                          variant="light"
                          leftSection={<IconCheck size={10} />}
                        >
                          <Trans>Found</Trans>
                        </Badge>
                      ) : state === 'NoMatch' ? (
                        <Badge size="xs" color="var(--neutral)" variant="light">
                          <Trans>No match</Trans>
                        </Badge>
                      ) : (
                        <Loader size={12} />
                      )}
                    </Group>
                  </Table.Td>
                  <Table.Td>
                    <Skeleton height={10} width="55%" radius="sm" animate={state === 'Searching'} />
                  </Table.Td>
                  <Table.Td>
                    <Skeleton height={10} width={44} radius="sm" animate={state === 'Searching'} />
                  </Table.Td>
                  <Table.Td>
                    <Skeleton height={10} width={26} radius="sm" animate={state === 'Searching'} />
                  </Table.Td>
                  <Table.Td>
                    <Skeleton height={10} width="45%" radius="sm" animate={state === 'Searching'} />
                  </Table.Td>
                  <Table.Td />
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      )}

      <Modal
        opened={removing !== null && !fallbackOpen}
        onClose={() => setRemoving(null)}
        title={removing ? t`Remove ${removingSourceName}?` : t`Remove source?`}
        centered
      >
        <Stack gap="md">
          <Text size="sm" c="var(--ink-3)">
            <Trans>
              Chapters not listed by another enabled source will be removed. Files downloaded from
              this source will be detached so they cannot be read as the correct chapter.
            </Trans>
          </Text>
          <Text size="sm" c="var(--ink-3)">
            <Trans>Detached CBZs stay in the Files section unless you choose to delete them.</Trans>
          </Text>

          {missingSnapshots.length > 0 && (
            <Alert color="var(--warn)" title={t`One refresh required`}>
              <Stack gap="xs">
                <Text size="sm">
                  <Trans>
                    {missingSnapshotNames} must record a chapter snapshot before Maki can safely
                    clean the list. Later source removals use the stored snapshots and make no
                    source requests.
                  </Trans>
                </Text>
                <Button
                  size="xs"
                  variant="light"
                  leftSection={<IconRefresh size={14} />}
                  loading={refreshSnapshots.isPending}
                  onClick={() => {
                    if (!removing) return
                    refreshSnapshots.mutate({ seriesId, excludeMappingId: removing.id }, {
                      onSuccess: () =>
                        notifications.show({
                          message: now`Chapter snapshots refreshed`,
                          color: 'var(--ok)',
                        }),
                    })
                  }}
                >
                  <Trans>Refresh chapters</Trans>
                </Button>
              </Stack>
            </Alert>
          )}

          {can('DeleteSeries') && (
            <Checkbox
              label={t`Also delete detached files from disk`}
              checked={deleteFiles}
              onChange={(event) => setDeleteFiles(event.currentTarget.checked)}
            />
          )}

          <Text size="sm" c="var(--danger)">
            <Trans>Reading progress and bookmarks for removed chapter rows will also be deleted.</Trans>
          </Text>
          <Group justify="space-between">
            <Button
              variant="subtle"
              color="var(--danger)"
              onClick={() => setFallbackOpen(true)}
            >
              <Trans>Remove without cleanup</Trans>
            </Button>
            <Group gap="xs">
              <Button variant="default" onClick={() => setRemoving(null)}>
                <Trans>Cancel</Trans>
              </Button>
              <Button
                color="var(--danger-fill)"
                leftSection={<IconTrash size={16} />}
                disabled={missingSnapshots.length > 0 || !removing}
                loading={removeMapping.isPending}
                onClick={() =>
                  removing &&
                  removeMapping.mutate(
                    { id: removing.id, seriesId, deleteFiles },
                    {
                      onSuccess: (result) => {
                        const kept = result.detachedFiles - result.deletedFiles
                        const failedCount = result.failedFileDeletions
                        const chaptersMsg = plural(result.removedChapters, {
                          one: 'Removed # unsupported chapter',
                          other: 'Removed # unsupported chapters',
                        })
                        const filesMsg = plural(kept, {
                          one: '# file left unlinked',
                          other: '# files left unlinked',
                        })
                        const failuresMsg =
                          failedCount > 0
                            ? plural(failedCount, {
                                one: '# could not be deleted',
                                other: '# could not be deleted',
                              })
                            : null
                        notifications.show({
                          message: failuresMsg
                            ? `${chaptersMsg}; ${filesMsg}; ${failuresMsg}`
                            : `${chaptersMsg}; ${filesMsg}`,
                          color: failedCount > 0 ? 'var(--warn)' : 'var(--ok)',
                        })
                        setRemoving(null)
                      },
                    },
                  )
                }
              >
                <Trans>Remove and clean up</Trans>
              </Button>
            </Group>
          </Group>
        </Stack>
      </Modal>

      <Modal
        opened={fallbackOpen}
        onClose={() => setFallbackOpen(false)}
        title={t`Remove source without cleanup?`}
        centered
      >
        <Stack gap="md">
          <Text size="sm" c="var(--ink-3)">
            <Trans>
              This removes only the source mapping. Existing chapter rows and files will stay
              exactly as they are and may need manual cleanup later.
            </Trans>
          </Text>
          <Text size="sm" c="var(--danger)">
            <Trans>This action cannot be undone.</Trans>
          </Text>
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setFallbackOpen(false)}>
              <Trans>Cancel</Trans>
            </Button>
            <Button
              color="var(--danger-fill)"
              loading={deleteMapping.isPending}
              onClick={() =>
                removing &&
                deleteMapping.mutate(
                  { id: removing.id, seriesId },
                  {
                    onSuccess: () => {
                      notifications.show({
                        message: now`Source removed without cleanup`,
                        color: 'var(--warn)',
                      })
                      setFallbackOpen(false)
                      setRemoving(null)
                    },
                  },
                )
              }
            >
              <Trans>Remove source only</Trans>
            </Button>
          </Group>
        </Stack>
      </Modal>

      <Modal
        opened={modalOpen}
        onClose={() => setModalOpen(false)}
        title={t`Link a source`}
        size="lg"
      >
        <Stack>
          <Group grow>
            <Select
              label={t`Source`}
              data={
                unmappedSources?.map((s) => ({ value: s.name, label: s.displayName })) ?? []
              }
              value={sourceName}
              onChange={setSourceName}
            />
            <TextInput
              label={t`Search or paste a series URL`}
              value={query}
              onChange={(e) => setQuery(e.currentTarget.value)}
              rightSection={isFetching || resolving ? <Loader size="xs" /> : null}
            />
          </Group>
          <Stack gap="xs">
            {pastedUrl && resolved && (
              <Card
                withBorder
                padding="xs"
                style={{ cursor: 'pointer' }}
                onClick={() => link(resolved.sourceName, resolved.sourceSeriesId, resolved.url)}
              >
                <Group wrap="nowrap">
                  {resolved.coverUrl && (
                    <Image src={resolved.coverUrl} w={40} h={60} radius="sm" fit="cover" alt="" />
                  )}
                  <div style={{ flex: 1, minWidth: 0 }}>
                    <Group gap="xs">
                      <Text fw={600} size="sm">
                        {resolved.title}
                      </Text>
                      <Badge size="xs" variant="light">
                        {resolved.displayName}
                      </Badge>
                    </Group>
                    <Text size="xs" c="var(--ink-3)" lineClamp={1}>
                      {resolved.url}
                    </Text>
                  </div>
                </Group>
              </Card>
            )}
            {pastedUrl && resolveError && (
              <Text c="var(--danger)" size="sm">
                {String(resolveError)}
              </Text>
            )}
            {!pastedUrl && results?.map((r) => (
              <Card
                key={r.sourceSeriesId}
                withBorder
                padding="xs"
                style={{ cursor: 'pointer' }}
                onClick={() => sourceName && link(sourceName, r.sourceSeriesId, r.url)}
              >
                <Group wrap="nowrap">
                  {r.coverUrl && (
                    <Image src={r.coverUrl} w={40} h={60} radius="sm" fit="cover" alt="" />
                  )}
                  <div style={{ flex: 1, minWidth: 0 }}>
                    <Text fw={600} size="sm">
                      {r.title}
                    </Text>
                    <Text size="xs" c="var(--ink-3)" lineClamp={1}>
                      {r.url}
                    </Text>
                  </div>
                  <Tooltip label={t`Open the page to check it's the right series`} withArrow>
                    <ActionIcon
                      component="a"
                      href={r.url}
                      target="_blank"
                      rel="noreferrer"
                      variant="subtle"
                      onClick={(e) => e.stopPropagation()}
                      aria-label={t`Open source page`}
                    >
                      <IconExternalLink size={16} />
                    </ActionIcon>
                  </Tooltip>
                </Group>
              </Card>
            ))}
            {sourceName && debounced.trim().length > 1 && results?.length === 0 && !isFetching && (
              <Text c="var(--ink-3)" size="sm">
                <Trans>No results.</Trans>
              </Text>
            )}
          </Stack>
        </Stack>
      </Modal>

      <SourceCompareModal
        seriesId={seriesId}
        opened={compareOpen}
        onClose={() => setCompareOpen(false)}
      />
    </>
  )
}

/**
 * The source's tier, then the median width and compression of its recently measured chapters of
 * the series. The tier is shown because best quality first compares it before the score.
 */
function MappingQuality({ quality, tier }: { quality: SourceQualityDto | undefined; tier: QualityTierName | undefined }) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  return (
    <Group gap={8} wrap="nowrap">
      {tier && (
        <Tooltip
          label={t`Best quality first compares a source's tier before its measured quality, in the order the series' upgrade profile ranks tiers.`}
          withArrow
          multiline
          w={260}
        >
          <Badge size="sm" variant="light" color={QUALITY_TIER_COLOR[tier]}>
            {renderLabel(QUALITY_TIER_LABELS[tier])}
          </Badge>
        </Tooltip>
      )}
      <MappingMeasurement quality={quality} />
    </Group>
  )
}

function MappingMeasurement({ quality }: { quality: SourceQualityDto | undefined }) {
  const { t, i18n } = useLingui()
  if (!quality) {
    return (
      <Tooltip label={t`Nothing downloaded or sampled from this source yet.`} withArrow>
        <Text size="xs" c="var(--ink-3)">
          –
        </Text>
      </Tooltip>
    )
  }

  const width = i18n.number(quality.medianWidth)
  const bpp = i18n.number(quality.bitsPerPixel, { maximumFractionDigits: 2 })
  const count = quality.samples
  const measured = formatDateTime(quality.latestUtc)
  const lines = [
    plural(count, {
      one: `Median of # measured chapter: ${width}px wide, ${bpp} bits per pixel (JPG equivalent).`,
      other: `Median of # measured chapters: ${width}px wide, ${bpp} bits per pixel (JPG equivalent).`,
    }),
    t`Last measured ${measured}.`,
  ]
  if (quality.resolutionPoints != null && quality.compressionPoints != null) {
    const resolution = signedNumber(i18n, quality.resolutionPoints)
    const compression = signedNumber(i18n, quality.compressionPoints)
    lines.push(t`Resolution ${resolution}, compression ${compression} under this series' profile.`)
  }
  if (!quality.reliable) {
    lines.push(t`Too few or too old to trust yet, so upgrade scans still sample this source first.`)
  }

  return (
    <Tooltip label={lines.join(' ')} withArrow multiline w={280}>
      <Text size="xs" c={quality.reliable ? undefined : 'var(--ink-3)'} style={{ whiteSpace: 'nowrap' }}>
        <Trans>
          {width}px · {bpp} bpp
        </Trans>
      </Text>
    </Tooltip>
  )
}

function signedNumber(i18n: { number: (n: number) => string }, n: number) {
  return n > 0 ? `+${i18n.number(n)}` : i18n.number(n)
}

/**
 * The languages one mapping lists chapters in.
 *
 * Adding a language is not just "more chapters": chapter identity is (number, language), so each
 * one adds its own row per chapter number and its own wanted/missing count, and each downloads to
 * its own file. Hence the warning rather than a bare picker.
 */
function MappingLanguages({
  mapping,
  supported,
  onChange,
}: {
  mapping: SourceMappingDto
  /** Whether the source honours a filter at all. */
  supported: boolean
  onChange: (languageFilter: string | null) => void
}) {
  const { t } = useLingui()
  const languageOptions = useLanguageOptions()
  const sourceLabel = useSourceLabel()
  const { sourceName: sourceKey } = mapping
  const sourceName = sourceLabel(sourceKey)
  // Null means the source default, which is English — not "every language". An untouched mapping
  // has to keep listing what it listed before.
  const selected = (mapping.languageFilter ?? 'en')
    .split(',')
    .map((code) => code.trim().toLowerCase())
    .filter(Boolean)

  if (!supported) {
    return (
      <Tooltip
        label={t`${sourceName} publishes one language per series, so there is nothing to filter.`}
        withArrow
        multiline
        w={240}
      >
        <Text size="xs" c="var(--ink-3)">
          {selected.join(', ') || 'en'}
        </Text>
      </Tooltip>
    )
  }

  return (
    <MultiSelect
      size="xs"
      w={150}
      data={languageOptions}
      value={selected}
      searchable
      // The picker is not a whitelist: a site can carry a code this list has never heard of, and
      // typing it has to work rather than being silently unavailable.
      onChange={(codes) => {
        const next = codes.length === 0 ? ['en'] : codes
        onChange(next.length === 1 && next[0] === 'en' ? null : next.join(','))
      }}
      description={
        selected.length > 1
          ? plural(selected.length, { one: '#× chapter row', other: '#× chapter rows' })
          : undefined
      }
    />
  )
}

/** Codes the multi-language sources here actually publish. */
const LANGUAGE_CODES = [
  'en', 'es', 'es-la', 'pt-br', 'fr', 'de', 'it', 'ru', 'pl', 'tr', 'ar', 'id', 'th', 'vi', 'ko',
  'zh', 'ja',
]

/**
 * Display labels for the codes above. A module evaluates once, so this holds descriptors rather
 * than strings and renders them through `useLanguageOptions` below.
 */
const LANGUAGE_LABELS: Record<string, MessageDescriptor> = {
  en: msg`English`,
  es: msg`Spanish`,
  'es-la': msg`Spanish (LATAM)`,
  'pt-br': msg`Portuguese (Br)`,
  fr: msg`French`,
  de: msg`German`,
  it: msg`Italian`,
  ru: msg`Russian`,
  pl: msg`Polish`,
  tr: msg`Turkish`,
  ar: msg`Arabic`,
  id: msg`Indonesian`,
  th: msg`Thai`,
  vi: msg`Vietnamese`,
  ko: msg`Korean`,
  zh: msg`Chinese`,
  ja: msg`Japanese`,
}

function useLanguageOptions() {
  const { _, i18n } = useLinguiReact()
  return useMemo(
    () => LANGUAGE_CODES.map((value) => ({ value, label: _(LANGUAGE_LABELS[value]) })),
    [_, i18n.locale],
  )
}
