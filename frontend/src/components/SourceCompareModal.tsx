import { useEffect, useMemo, useRef, useState } from 'react'
import {
  ActionIcon,
  Alert,
  Badge,
  Box,
  Button,
  Card,
  Group,
  Image,
  Modal,
  ScrollArea,
  Select,
  Skeleton,
  Stack,
  Switch,
  Text,
  Tooltip,
} from '@mantine/core'
import {
  IconAlertTriangle,
  IconChevronLeft,
  IconChevronRight,
  IconGripVertical,
  IconPhotoCheck,
  IconRefresh,
  IconX,
} from '@tabler/icons-react'
import { notifications } from '@mantine/notifications'
import {
  useChapters,
  useDownloadChapterFrom,
  useRedownloadFromSource,
  useReorderMappings,
  useSaveSourcePriority,
  useSourceCompare,
  useSourcePriority,
  useSources,
  useStartSourceCompare,
} from '../api/hooks'
import { useAuth } from '../auth/AuthProvider'
import type { ComparePanel } from '../api/types'
import { Trans, useLingui } from '@lingui/react/macro'
import { t as now, plural } from '@lingui/core/macro'

const COLUMN_WIDTH = 300

function formatSize(bytes: number): string {
  const units = ['B', 'KB', 'MB', 'GB']
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit++
  }
  return `${value.toFixed(unit === 0 ? 0 : 1)} ${units[unit]}`
}

/** The chapter a `pick` run is about: one fixed chapter, re-fetched from whichever panel wins. */
export interface PickChapter {
  id: number
  number: number
  label: string
  /** What the file on disk came from, so its panel can say so. Null for imports and torrents. */
  currentSourceName: string | null
}

/**
 * Side-by-side view of the same chapter as each of a series' sources scans it, with the columns
 * draggable into a preference order that is written back as this series' source priority.
 *
 * In `pick` mode the same grid answers a narrower question: this one chapter came out badly, which
 * source has a better scan of it. Ranking is out of the way (no drag, no chapter picker, no save)
 * and each column offers to fetch that chapter from itself, overwriting the file.
 *
 * Source names are hidden by default: seeing "MangaDex" above a panel is exactly the kind of prior
 * that the comparison exists to get around. They reveal on the toggle, and after the order is saved.
 */
export function SourceCompareModal({
  seriesId,
  opened,
  onClose,
  mode = 'compare',
  chapter,
}: {
  seriesId: number
  opened: boolean
  onClose: () => void
  mode?: 'compare' | 'pick'
  chapter?: PickChapter
}) {
  const { can } = useAuth()
  const { t, i18n } = useLingui()
  const start = useStartSourceCompare()
  const isAdmin = can('Admin')
  // Only poll once the job exists: a GET that lands first answers 404, and a query with no data
  // never schedules another interval.
  const { data: snapshot } = useSourceCompare(seriesId, opened && start.isSuccess)
  const reorder = useReorderMappings()
  const { data: globalPriority } = useSourcePriority(isAdmin)
  const saveGlobal = useSaveSourcePriority()
  const { data: chapters } = useChapters(seriesId)
  const { data: allSources } = useSources()
  const redownload = useRedownloadFromSource()
  const downloadFrom = useDownloadChapterFrom()
  const pick = mode === 'pick' && chapter ? chapter : null

  const [order, setOrder] = useState<number[]>([])
  // Until the user drags something, failed panels are floated to the back. Seeding can't do it —
  // at seed time every panel is still listing — and a source whose pages nobody could see is never
  // the one you meant to rank first.
  const [ranked, setRanked] = useState(false)
  const [blind, setBlind] = useState(true)
  const [saved, setSaved] = useState(false)
  // Where the zoom overlay is looking: a cell in the grid, so it can walk sideways across
  // sources at the same page. A bare URL couldn't answer "what's the same page on the next one".
  const [zoom, setZoom] = useState<{ panel: number; page: number } | null>(null)

  // The real order only changes on drop. While dragging, columns are shifted purely visually
  // (transform) to open a gap; reordering the DOM mid-drag makes columns slide past the stationary
  // cursor and re-trigger, which feeds back on itself.
  const [dragFromIndex, setDragFromIndex] = useState<number | null>(null)
  const [hoverIndex, setHoverIndex] = useState<number | null>(null)
  const rowRef = useRef<HTMLDivElement>(null)

  // Each open is a fresh comparison — sources re-scan chapters and the previous run's files are gone.
  useEffect(() => {
    if (opened) {
      setBlind(true)
      setSaved(false)
      setZoom(null)
      setOrder([])
      setRanked(false)
      start.mutate({ seriesId, chapterNumber: pick?.number })
    }
    // start.mutate is stable; re-running this on every render would restart the job in a loop.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [opened, seriesId, pick?.number])

  // Seed the ranking from the server's order (current priority) once, then leave it to the user —
  // reseeding on every poll would undo a drag the moment the next panel finished fetching.
  useEffect(() => {
    if (snapshot && order.length === 0) {
      setOrder(snapshot.panels.map((p) => p.mappingId))
    }
  }, [snapshot, order.length])

  /** Bytes across every page this source actually returned. Missing rows count for nothing. */
  const weightOf = (panel: ComparePanel) =>
    panel.pages.reduce((sum, page) => sum + (page?.bytes ?? 0), 0)

  /**
   * Starting order: heaviest first. Over the same pages, the source shipping more bytes is usually
   * the one that compressed them least, so this puts the likeliest winner in front instead of
   * whatever the old priority happened to be.
   *
   * Only applied once every panel has settled. Panels land one at a time, and re-sorting on each
   * arrival would have the columns dancing about while the user is trying to look at them.
   */
  const startingOrder = useMemo(() => {
    const list = [...(snapshot?.panels ?? [])]
    if (snapshot && !snapshot.running) {
      // Stable sort, so sources that tie keep the order the server sent them in.
      list.sort((a, b) => weightOf(b) - weightOf(a))
      return list
    }
    return [...list.filter((p) => p.status !== 'failed'), ...list.filter((p) => p.status === 'failed')]
  }, [snapshot])

  const blindLabels = useMemo(() => {
    // Lettered in the order the columns appear, so A is the heaviest. Keyed on the panel rather
    // than its rank, so dragging never renames a column mid-comparison.
    const labels = new Map<number, string>()
    startingOrder.forEach((p, i) => {
      const letter = String.fromCharCode(65 + i)
      labels.set(p.mappingId, t`Source ${letter}`)
    })
    return labels
    // `i18n.locale` is not read directly above, but `t` closes over the active catalogue, so this
    // has to be a dep or a language switch would keep handing back the previous letters' labels.
  }, [startingOrder, t, i18n.locale])

  const panels = useMemo(() => {
    if (!snapshot) return []
    // Until the user drags something, the weight order stands. After that their order is the only
    // one that matters, and re-sorting would undo the drag they just made.
    if (!ranked) return startingOrder

    const byId = new Map(snapshot.panels.map((p) => [p.mappingId, p]))
    const known = order.map((id) => byId.get(id)).filter((p): p is ComparePanel => p !== undefined)
    return [...known, ...snapshot.panels.filter((p) => !order.includes(p.mappingId))]
  }, [snapshot, order, ranked, startingOrder])

  function handleRowDragOver(e: React.DragEvent) {
    e.preventDefault()
    if (dragFromIndex === null || !rowRef.current || panels.length === 0) return
    const rect = rowRef.current.getBoundingClientRect()
    const raw = Math.floor((e.clientX - rect.left) / COLUMN_WIDTH)
    setHoverIndex(Math.min(Math.max(raw, 0), panels.length - 1))
  }

  function commitDrag() {
    if (dragFromIndex !== null && hoverIndex !== null && dragFromIndex !== hoverIndex) {
      const next = panels.map((p) => p.mappingId)
      const [moved] = next.splice(dragFromIndex, 1)
      next.splice(hoverIndex, 0, moved)
      setOrder(next)
      setRanked(true)
    }
    setDragFromIndex(null)
    setHoverIndex(null)
  }

  const save = () => {
    reorder.mutate(
      { seriesId, orderedMappingIds: panels.map((p) => p.mappingId) },
      {
        onSuccess: () => {
          setSaved(true)
          setBlind(false)
          notifications.show({ message: now`Source priority updated`, color: 'var(--ok)' })
        },
      },
    )
  }

  // "Default for new series" means the compared sources lead the global order; sources that weren't
  // part of this comparison keep their existing relative places behind them. The disabled list is
  // passed through untouched — that switch is nothing to do with ranking.
  const applyGlobally = () => {
    if (!globalPriority) return
    const compared = panels.map((p) => p.sourceName)
    saveGlobal.mutate(
      {
        order: [...compared, ...globalPriority.order.filter((n) => !compared.includes(n))],
        disabled: globalPriority.disabled,
      },
      {
        onSuccess: () =>
          notifications.show({ message: now`Default source order updated`, color: 'var(--ok)' }),
      },
    )
  }


  // Sources that actually have an image at a given page index. Left/right walks this, not the raw
  // panel list — stepping onto a failed source would blank the screen mid-comparison.
  const panelsWithPage = (pageIndex: number) =>
    panels.map((p, index) => ({ p, index })).filter(({ p }) => p.pages[pageIndex] != null)

  const zoomStep = (axis: 'source' | 'page', delta: number) => {
    setZoom((current) => {
      if (!current) return current
      if (axis === 'page') {
        const pages = panels[current.panel]?.pages ?? []
        // Step over rows this source has no page for rather than stopping dead on one.
        for (let page = current.page + delta; page >= 0 && page < pages.length; page += delta) {
          if (pages[page] != null) {
            return { ...current, page }
          }
        }
        return current
      }

      const row = panelsWithPage(current.page)
      const at = row.findIndex(({ index }) => index === current.panel)
      const next = row[at + delta]
      return next ? { ...current, panel: next.index } : current
    })
  }

  // Arrow keys are the whole point: flicking left/right between sources on the *same* page is how
  // you actually see the difference between two scans. Comparing them by scrolling two columns
  // side by side never lines the pages up.
  useEffect(() => {
    if (!zoom) return
    const onKey = (e: KeyboardEvent) => {
      const axis = e.key === 'ArrowLeft' || e.key === 'ArrowRight' ? 'source' : 'page'
      const delta =
        e.key === 'ArrowLeft' || e.key === 'ArrowUp'
          ? -1
          : e.key === 'ArrowRight' || e.key === 'ArrowDown'
            ? 1
            : 0
      if (delta === 0) return
      e.preventDefault()
      zoomStep(axis, delta)
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
    // zoomStep closes over `panels`, which changes identity on every poll; keying the listener on
    // whether the overlay is open (not on the position) keeps it from being rebound constantly.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [zoom !== null, panels])

  const zoomPanel = zoom ? panels[zoom.panel] : undefined
  const zoomPage = zoom ? zoomPanel?.pages[zoom.page] : undefined
  const zoomRow = zoom ? panelsWithPage(zoom.page) : []
  const zoomRank = zoomRow.findIndex(({ index }) => index === zoom?.panel)
  const zoomRowNumber = (zoom?.page ?? 0) + 1
  const zoomTotalPages = zoomPanel?.pages.length ?? 0

  // The point of ranking sources is the files you end up with, and those were downloaded before
  // the ranking existed. Anything on disk from a source other than the new favourite is a candidate
  // to fetch again; files imported from disk carry no source name and are never touched.
  const chapterNumber = snapshot?.chapterNumber
  const winner = panels[0]
  const winnerDisplayName = winner?.displayName
  const staleChapters = (chapters ?? []).filter(
    (c) =>
      c.hasFile &&
      c.fileSourceName !== null &&
      c.fileSourceName !== winner?.sourceName &&
      // "import" and "torrent:{indexer}" also live in this field and are not ours to replace, so
      // count only files that came from a source the comparison could actually have ranked.
      (allSources ?? []).some((x) => x.name === c.fileSourceName),
  ).length

  const runRedownload = () => {
    if (!winner) return
    const { displayName } = winner
    redownload.mutate(
      { seriesId, sourceName: winner.sourceName },
      {
        onSuccess: (result) => {
          const message =
            result.queued === 0
              ? now`Nothing queued: ${displayName} doesn't list those chapters.`
              : result.unavailable > 0
                ? `${plural(result.queued, {
                    one: 'Queued # chapter.',
                    other: 'Queued # chapters.',
                  })} ${plural(result.unavailable, {
                    one: `# isn't on ${displayName}, so it was left as it is.`,
                    other: `# aren't on ${displayName}, so they were left as they are.`,
                  })}`
                : plural(result.queued, {
                    one: `Queued # chapter from ${displayName}.`,
                    other: `Queued # chapters from ${displayName}.`,
                  })
          notifications.show({
            color: result.queued > 0 ? 'var(--ok)' : undefined,
            message,
          })
        },
      },
    )
  }

  const pickPanel = (panel: ComparePanel) => {
    if (!pick) return
    const label = pick.label
    downloadFrom.mutate(
      { chapterId: pick.id, sourceMappingId: panel.mappingId },
      {
        onSuccess: () => {
          notifications.show({ color: 'var(--ok)', message: now`Fetching ${label} again. The file is replaced when it lands.` })
          onClose()
        },
      },
    )
  }

  const chapterOptions = (snapshot?.commonChapters ?? []).map((n) => ({
    value: String(n),
    label: t`Chapter ${n}`,
  }))

  return (
    <>
      <Modal
        opened={opened}
        onClose={onClose}
        size="min(1180px, calc(100vw - 3rem))"
        title={pick ? t`Find a better copy of ${pick.label}` : t`Compare sources`}
        // Full height with the column row taking what's left, so the row scrolls inside the modal
        // and its horizontal scrollbar stays on screen. Otherwise the modal itself scrolls and a
        // webtoon's tall pages push that scrollbar far below the fold.
        styles={{
          content: { height: 'calc(100dvh - var(--modal-y-offset) * 2)' },
          body: { paddingTop: 0, flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' },
        }}
        // Both modals hear the same Escape, so without this one keypress closes the zoom *and*
        // throws away the comparison behind it.
        closeOnEscape={zoom === null}
      >
        <Stack gap="md" style={{ flex: 1, minHeight: 0 }}>
          <Text size="sm" c="var(--ink-3)">
            {pick ? (
              <Trans>
                This chapter as each source scans it, heaviest first. Pick the one that looks best
                and Maki downloads it again from there, replacing the file you have.
              </Trans>
            ) : (
              <Trans>
                The same chapter as each source scans it, heaviest first. Drag the columns so your
                favourite is first, then save: that becomes the order chapters download in for this
                series.
              </Trans>
            )}
          </Text>

          <Group justify="space-between" wrap="wrap" gap="sm">
            <Group gap="sm">
              {!pick && chapterOptions.length > 0 && (
                <Select
                  size="xs"
                  w={160}
                  label={t`Sample chapter`}
                  data={chapterOptions}
                  value={snapshot?.chapterNumber != null ? String(snapshot.chapterNumber) : null}
                  onChange={(v) => {
                    if (v) {
                      setOrder([])
                      start.mutate({ seriesId, chapterNumber: Number(v) })
                    }
                  }}
                />
              )}
            </Group>
            <Group gap="md">
              {snapshot?.pagesAligned && (
                <Tooltip
                  label={t`Sources disagree on where a chapter starts: a credit page here, a colour cover there. Pages are matched by image content, so each row is the same drawing in every column. A source whose scan matches nothing is left at its own first page.`}
                  withArrow
                  multiline
                  w={300}
                >
                  <Badge size="sm" variant="light" color="var(--ok)" leftSection={<IconPhotoCheck size={12} />}>
                    <Trans>Pages matched</Trans>
                  </Badge>
                </Tooltip>
              )}
              <Switch
                size="xs"
                checked={blind}
                label={t`Hide source names`}
                description={t`Judge the scans, not the site`}
                onChange={(e) => setBlind(e.currentTarget.checked)}
              />
            </Group>
          </Group>

          {snapshot?.mixedChapters && (
            <Alert color="var(--warn)" icon={<IconAlertTriangle size={16} />}>
              <Trans>
                Not every source carries chapter {chapterNumber}, so some columns are
                showing their own first chapter instead. Each column says which one it got.
              </Trans>
            </Alert>
          )}

          <ScrollArea type="auto" offsetScrollbars style={{ flex: 1, minHeight: 0 }}>
            <Group
              gap={0}
              align="stretch"
              wrap="nowrap"
              ref={rowRef}
              onDragOver={handleRowDragOver}
              style={{ minHeight: 200 }}
            >
              {panels.map((panel, i) => {
                const { chapterLabel } = panel
                const weight = formatSize(weightOf(panel))
                let shift = 0
                if (dragFromIndex !== null && hoverIndex !== null && i !== dragFromIndex) {
                  if (dragFromIndex < hoverIndex && i > dragFromIndex && i <= hoverIndex) shift = -1
                  else if (dragFromIndex > hoverIndex && i >= hoverIndex && i < dragFromIndex) shift = 1
                }
                return (
                  <Box
                    key={panel.mappingId}
                    w={COLUMN_WIDTH}
                    px={6}
                    style={{
                      flex: `0 0 ${COLUMN_WIDTH}px`,
                      transform: shift ? `translateX(${shift * COLUMN_WIDTH}px)` : undefined,
                      transition: 'transform var(--dur-base) var(--ease)',
                      opacity: dragFromIndex === i ? 0 : 1,
                      pointerEvents:
                        dragFromIndex !== null && i !== dragFromIndex ? 'none' : undefined,
                    }}
                  >
                    <Card
                      withBorder
                      radius="md"
                      padding="xs"
                      h="100%"
                      draggable={!pick}
                      onDragStart={(e) => {
                        if (pick) return
                        // setDragImage on the live node keeps tracking it, so the ghost goes
                        // invisible along with the column once opacity flips to 0. A detached
                        // clone is an independent snapshot.
                        const original = e.currentTarget
                        const clone = original.cloneNode(true) as HTMLElement
                        clone.style.position = 'fixed'
                        clone.style.top = '-9999px'
                        clone.style.left = '-9999px'
                        clone.style.width = `${original.offsetWidth}px`
                        clone.style.pointerEvents = 'none'
                        document.body.appendChild(clone)
                        e.dataTransfer.setDragImage(clone, e.nativeEvent.offsetX, 20)
                        setTimeout(() => document.body.removeChild(clone), 0)
                        setDragFromIndex(i)
                        setHoverIndex(i)
                      }}
                      onDragEnd={pick ? undefined : commitDrag}
                      style={{ cursor: pick ? undefined : 'grab' }}
                    >
                      <Group gap={6} wrap="nowrap" mb="xs">
                        {!pick && <IconGripVertical size={14} style={{ opacity: 0.5 }} />}
                        <Badge size="sm" variant="filled">
                          #{i + 1}
                        </Badge>
                        <Text size="sm" fw={500} truncate>
                          {blind ? (blindLabels.get(panel.mappingId) ?? '?') : panel.displayName}
                        </Text>
                        {pick && pick.currentSourceName === panel.sourceName && (
                          <Badge size="xs" variant="light" color="var(--neutral)">
                            <Trans>Current copy</Trans>
                          </Badge>
                        )}
                        {snapshot?.mixedChapters && panel.chapterLabel && (
                          <Badge size="xs" variant="light" color="var(--neutral)">
                            <Trans>Ch. {chapterLabel}</Trans>
                          </Badge>
                        )}
                        {snapshot?.pagesAligned && panel.status === 'ready' && !panel.aligned && (
                          <Tooltip
                            label={t`This source's images don't match the others, usually a different edition or scanlation. Its pages are shown as served, so the rows don't line up with the other columns.`}
                            withArrow
                            multiline
                            w={280}
                          >
                            <Badge size="xs" variant="light" color="var(--warn)">
                              <Trans>Unmatched</Trans>
                            </Badge>
                          </Tooltip>
                        )}
                      </Group>

                      {panel.status === 'ready' && panel.pages.some((x) => x !== null) && (
                        <Tooltip
                          label={t`Total bytes across the pages shown. Over the same pages it tracks how hard the source compressed them, which is why the columns start in this order, but a source that upscales its scans is bigger without being better.`}
                          withArrow
                          multiline
                          w={280}
                        >
                          <Text size="xs" c="var(--ink-3)" mb={6}>
                            <Trans>{weight} total</Trans>
                          </Text>
                        </Tooltip>
                      )}

                      {pick && panel.status === 'ready' && (
                        <Stack gap={6} mb="xs">
                          {panel.pageCount !== null && (
                            <Text size="xs" c="var(--ink-3)">
                              {plural(panel.pageCount, { one: '# page', other: '# pages' })}
                            </Text>
                          )}
                          <Button
                            size="xs"
                            variant="light"
                            fullWidth
                            loading={
                              downloadFrom.isPending &&
                              downloadFrom.variables?.sourceMappingId === panel.mappingId
                            }
                            onClick={() => pickPanel(panel)}
                          >
                            <Trans>Use this copy</Trans>
                          </Button>
                        </Stack>
                      )}

                      {panel.status === 'failed' ? (
                        <Text size="xs" c="var(--ink-3)">
                          {panel.error ?? <Trans>Failed</Trans>}
                        </Text>
                      ) : panel.status === 'ready' ? (
                        <Stack gap="xs">
                          {panel.pages.map((page, pageIndex) =>
                            page ? (
                              <Box key={page.url}>
                                <Image
                                  src={page.url}
                                  alt=""
                                  fit="contain"
                                  draggable={false}
                                  style={{ cursor: 'zoom-in' }}
                                  onMouseDown={(e) => e.stopPropagation()}
                                  onClick={() => setZoom({ panel: i, page: pageIndex })}
                                />
                                <Text size="10px" c="var(--ink-3)" ta="center" mt={2}>
                                  {page.width ? `${page.width}×${page.height} · ` : ''}
                                  {formatSize(page.bytes)}
                                </Text>
                              </Box>
                            ) : (
                              <Box
                                // A gap's identity really is its row: there is no page to key on.
                                key={`gap-${pageIndex}`}
                                h={120}
                                style={{
                                  display: 'flex',
                                  alignItems: 'center',
                                  justifyContent: 'center',
                                  border: '1px dashed var(--ink-3)',
                                  borderRadius: 'var(--mantine-radius-xs)',
                                  opacity: 0.4,
                                }}
                              >
                                <Text size="xs" c="var(--ink-3)">
                                  <Trans>No matching page</Trans>
                                </Text>
                              </Box>
                            ),
                          )}
                        </Stack>
                      ) : (
                        <Stack gap="xs">
                          <Skeleton height={320} radius="sm" />
                          <Text size="xs" c="var(--ink-3)" ta="center">
                            {panel.status === 'listing' ? (
                              <Trans>Looking up chapters…</Trans>
                            ) : (
                              <Trans>Fetching pages…</Trans>
                            )}
                          </Text>
                        </Stack>
                      )}
                    </Card>
                  </Box>
                )
              })}
            </Group>
          </ScrollArea>

          <Group justify="space-between">
            <Group gap="xs">
              {!pick && saved && staleChapters > 0 && winner && (
                <Tooltip
                  label={`${plural(staleChapters, {
                    one: '# downloaded chapter came from another source.',
                    other: '# downloaded chapters came from another source.',
                  })} ${t`Chapters ${winnerDisplayName} doesn't carry are left alone, as are files imported from disk.`}`}
                  withArrow
                  multiline
                  w={280}
                >
                  <Button
                    size="xs"
                    variant="light"
                    leftSection={<IconRefresh size={14} />}
                    loading={redownload.isPending}
                    onClick={runRedownload}
                  >
                    <Trans>
                      Re-download {staleChapters} from {winnerDisplayName}
                    </Trans>
                  </Button>
                </Tooltip>
              )}
              {!pick && saved && isAdmin && (
                <Tooltip
                  label={t`Puts these sources, in this order, at the front of the global priority list used when new series auto-match.`}
                  withArrow
                  multiline
                  w={260}
                >
                  <Button
                    size="xs"
                    variant="default"
                    loading={saveGlobal.isPending}
                    onClick={applyGlobally}
                  >
                    <Trans>Also make this my default order</Trans>
                  </Button>
                </Tooltip>
              )}
            </Group>
            <Group gap="xs">
              <Button variant="default" onClick={onClose}>
                <Trans>Close</Trans>
              </Button>
              {!pick && (
                <Button
                  loading={reorder.isPending}
                  disabled={panels.length === 0}
                  onClick={save}
                >
                  <Trans>Save order</Trans>
                </Button>
              )}
            </Group>
          </Group>
        </Stack>
      </Modal>

      {/* Fit-to-column is fine for layout but useless for judging a scan, so a click gives the
          image at its own resolution — and then left/right swaps that same page between sources. */}
      <Modal
        opened={zoom !== null}
        onClose={() => setZoom(null)}
        size="auto"
        withCloseButton={false}
        padding={0}
        centered
        styles={{ content: { overflow: 'hidden' } }}
      >
        {zoomPage && zoomPanel && (
          <Stack gap={0}>
            <Group justify="space-between" wrap="nowrap" px="sm" py={6} gap="md">
              <Group gap="xs" wrap="nowrap">
                <ActionIcon
                  variant="subtle"
                  size="sm"
                  disabled={zoomRank <= 0}
                  onClick={() => zoomStep('source', -1)}
                  aria-label={t`Previous source`}
                >
                  <IconChevronLeft size={16} />
                </ActionIcon>
                <Text size="sm" fw={500} miw={110} ta="center">
                  {blind ? (blindLabels.get(zoomPanel.mappingId) ?? '?') : zoomPanel.displayName}
                </Text>
                <ActionIcon
                  variant="subtle"
                  size="sm"
                  disabled={zoomRank < 0 || zoomRank >= zoomRow.length - 1}
                  onClick={() => zoomStep('source', 1)}
                  aria-label={t`Next source`}
                >
                  <IconChevronRight size={16} />
                </ActionIcon>
              </Group>
              <Text size="xs" c="var(--ink-3)">
                <Trans>
                  Row {zoomRowNumber} of {zoomTotalPages}
                </Trans>
                {zoomPage.width ? ` · ${zoomPage.width}×${zoomPage.height}` : ''} ·{' '}
                {formatSize(zoomPage.bytes)}
              </Text>
              <ActionIcon
                variant="subtle"
                size="sm"
                onClick={() => setZoom(null)}
                aria-label={t`Close`}
              >
                <IconX size={16} />
              </ActionIcon>
            </Group>

            <Box style={{ maxHeight: '80vh', overflow: 'auto' }}>
              <img
                src={zoomPage.url}
                alt=""
                style={{ maxWidth: '90vw', display: 'block' }}
              />
            </Box>

            <Text size="xs" c="var(--ink-3)" ta="center" px="sm" py={6}>
              <Trans>← → swap source</Trans> · <Trans>↑ ↓ change page</Trans> · <Trans>Esc closes</Trans>
            </Text>
          </Stack>
        )}
      </Modal>
    </>
  )
}
