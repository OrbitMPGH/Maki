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
  IconX,
} from '@tabler/icons-react'
import { notifications } from '@mantine/notifications'
import {
  useReorderMappings,
  useSaveSourcePriority,
  useSourceCompare,
  useSourcePriority,
  useStartSourceCompare,
} from '../api/hooks'
import { useAuth } from '../auth/AuthProvider'
import type { ComparePanel } from '../api/types'

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

/** A, B, C… so the ranking is done on what the pages look like, not on which site they came from. */
function blindLabel(index: number): string {
  return `Source ${String.fromCharCode(65 + index)}`
}

/**
 * Side-by-side view of the same chapter as each of a series' sources scans it, with the columns
 * draggable into a preference order that is written back as this series' source priority.
 *
 * Source names are hidden by default: seeing "MangaDex" above a panel is exactly the kind of prior
 * that the comparison exists to get around. They reveal on the toggle, and after the order is saved.
 */
export function SourceCompareModal({
  seriesId,
  opened,
  onClose,
}: {
  seriesId: number
  opened: boolean
  onClose: () => void
}) {
  const { can } = useAuth()
  const start = useStartSourceCompare()
  const isAdmin = can('Admin')
  // Only poll once the job exists: a GET that lands first answers 404, and a query with no data
  // never schedules another interval.
  const { data: snapshot } = useSourceCompare(seriesId, opened && start.isSuccess)
  const reorder = useReorderMappings()
  const { data: globalPriority } = useSourcePriority(isAdmin)
  const saveGlobal = useSaveSourcePriority()

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
      start.mutate({ seriesId })
    }
    // start.mutate is stable; re-running this on every render would restart the job in a loop.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [opened, seriesId])

  // Seed the ranking from the server's order (current priority) once, then leave it to the user —
  // reseeding on every poll would undo a drag the moment the next panel finished fetching.
  useEffect(() => {
    if (snapshot && order.length === 0) {
      setOrder(snapshot.panels.map((p) => p.mappingId))
    }
  }, [snapshot, order.length])

  const blindLabels = useMemo(() => {
    const labels = new Map<number, string>()
    snapshot?.panels.forEach((p, i) => labels.set(p.mappingId, blindLabel(i)))
    return labels
  }, [snapshot])

  const panels = useMemo(() => {
    if (!snapshot) return []
    const byId = new Map(snapshot.panels.map((p) => [p.mappingId, p]))
    const known = order.map((id) => byId.get(id)).filter((p): p is ComparePanel => p !== undefined)
    // Anything the order doesn't know about yet (first render of a fresh job) goes on the end.
    const all = [...known, ...snapshot.panels.filter((p) => !order.includes(p.mappingId))]
    return ranked
      ? all
      : [...all.filter((p) => p.status !== 'failed'), ...all.filter((p) => p.status === 'failed')]
  }, [snapshot, order, ranked])

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
          notifications.show({ message: 'Source priority updated', color: 'green' })
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
          notifications.show({ message: 'Default source order updated', color: 'green' }),
      },
    )
  }


  // Sources that actually have an image at a given page index. Left/right walks this, not the raw
  // panel list — stepping onto a failed source would blank the screen mid-comparison.
  const panelsWithPage = (pageIndex: number) =>
    panels.map((p, index) => ({ p, index })).filter(({ p }) => p.pages.length > pageIndex)

  const zoomStep = (axis: 'source' | 'page', delta: number) => {
    setZoom((current) => {
      if (!current) return current
      if (axis === 'page') {
        const pages = panels[current.panel]?.pages.length ?? 0
        const page = current.page + delta
        return page >= 0 && page < pages ? { ...current, page } : current
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

  const chapterOptions = (snapshot?.commonChapters ?? []).map((n) => ({
    value: String(n),
    label: `Chapter ${n}`,
  }))

  return (
    <>
      <Modal
        opened={opened}
        onClose={onClose}
        size="95%"
        title="Compare sources"
        styles={{ body: { paddingTop: 0 } }}
        // Both modals hear the same Escape, so without this one keypress closes the zoom *and*
        // throws away the comparison behind it.
        closeOnEscape={zoom === null}
      >
        <Stack gap="md">
          <Text size="sm" c="dimmed">
            The same chapter as each source scans it. Drag the columns so your favourite is first,
            then save: that becomes the order chapters download in for this series.
          </Text>

          <Group justify="space-between" wrap="wrap" gap="sm">
            <Group gap="sm">
              {chapterOptions.length > 0 && (
                <Select
                  size="xs"
                  w={160}
                  label="Sample chapter"
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
                  label="Sources disagree on where a chapter starts — a credit page here, a colour cover there. Pages are matched by image content, so each row is the same drawing in every column. A source whose scan matches nothing is left at its own first page."
                  withArrow
                  multiline
                  w={300}
                >
                  <Badge size="sm" variant="light" color="teal" leftSection={<IconPhotoCheck size={12} />}>
                    Pages matched
                  </Badge>
                </Tooltip>
              )}
              <Switch
                size="xs"
                checked={blind}
                label="Hide source names"
                description="Judge the scans, not the site"
                onChange={(e) => setBlind(e.currentTarget.checked)}
              />
            </Group>
          </Group>

          {snapshot?.mixedChapters && (
            <Alert color="yellow" icon={<IconAlertTriangle size={16} />}>
              Not every source carries chapter {snapshot.chapterNumber}, so some columns are showing
              their own first chapter instead. Each column says which one it got.
            </Alert>
          )}

          <ScrollArea type="auto" offsetScrollbars>
            <Group
              gap={0}
              align="stretch"
              wrap="nowrap"
              ref={rowRef}
              onDragOver={handleRowDragOver}
              style={{ minHeight: 200 }}
            >
              {panels.map((panel, i) => {
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
                      transition: 'transform 150ms ease',
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
                      draggable
                      onDragStart={(e) => {
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
                      onDragEnd={commitDrag}
                      style={{ cursor: 'grab' }}
                    >
                      <Group gap={6} wrap="nowrap" mb="xs">
                        <IconGripVertical size={14} style={{ opacity: 0.5 }} />
                        <Badge size="sm" variant="filled">
                          #{i + 1}
                        </Badge>
                        <Text size="sm" fw={500} truncate>
                          {blind ? (blindLabels.get(panel.mappingId) ?? '?') : panel.displayName}
                        </Text>
                        {snapshot?.mixedChapters && panel.chapterLabel && (
                          <Badge size="xs" variant="light" color="gray">
                            Ch. {panel.chapterLabel}
                          </Badge>
                        )}
                      </Group>

                      {panel.status === 'failed' ? (
                        <Text size="xs" c="dimmed">
                          {panel.error ?? 'Failed'}
                        </Text>
                      ) : panel.status === 'ready' ? (
                        <Stack gap="xs">
                          {panel.pages.map((page, pageIndex) => (
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
                              <Text size="10px" c="dimmed" ta="center" mt={2}>
                                {page.width}×{page.height} · {formatSize(page.bytes)}
                              </Text>
                            </Box>
                          ))}
                        </Stack>
                      ) : (
                        <Stack gap="xs">
                          <Skeleton height={320} radius="sm" />
                          <Text size="xs" c="dimmed" ta="center">
                            {panel.status === 'listing' ? 'Looking up chapters…' : 'Fetching pages…'}
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
              {saved && isAdmin && (
                <Tooltip
                  label="Puts these sources, in this order, at the front of the global priority list used when new series auto-match."
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
                    Also make this my default order
                  </Button>
                </Tooltip>
              )}
            </Group>
            <Group gap="xs">
              <Button variant="default" onClick={onClose}>
                Close
              </Button>
              <Button
                loading={reorder.isPending}
                disabled={panels.length === 0}
                onClick={save}
              >
                Save order
              </Button>
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
                  aria-label="Previous source"
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
                  aria-label="Next source"
                >
                  <IconChevronRight size={16} />
                </ActionIcon>
              </Group>
              <Text size="xs" c="dimmed">
                Page {(zoom?.page ?? 0) + 1} of {zoomPanel.pages.length} · {zoomPage.width}×
                {zoomPage.height} · {formatSize(zoomPage.bytes)}
              </Text>
              <ActionIcon variant="subtle" size="sm" onClick={() => setZoom(null)} aria-label="Close">
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

            <Text size="xs" c="dimmed" ta="center" px="sm" py={6}>
              ← → swap source · ↑ ↓ change page · Esc closes
            </Text>
          </Stack>
        )}
      </Modal>
    </>
  )
}
