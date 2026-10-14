import { memo, startTransition, useEffect, useState } from 'react'
import type { ReactNode } from 'react'
import { plural, t as staticT } from '@lingui/core/macro'
import { Trans, useLingui } from '@lingui/react/macro'
import type { MessageDescriptor } from '@lingui/core'
import { ActionIcon, Badge, Group, Loader, Menu, Progress, Skeleton, Switch, Table, Text, Tooltip } from '@mantine/core'
import {
  IconBook,
  IconChevronDown,
  IconDeviceTv,
  IconDotsVertical,
  IconDownload,
  IconEye,
  IconEyeOff,
  IconLink,
  IconMinus,
} from '@tabler/icons-react'
import { queueErrorMessage } from '../../api/queue'
import type { ChapterProgressDto, ChapterReadState } from '../../api/reader'
import type { ChapterDto, QueueItemDto } from '../../api/types'
import { formatCalendarDate } from '../../format'
import type { AnimeMarker, AnimeSpan } from '../../lib/animeCoverage'
import { onPressKey, pressable } from '../../lib/pressable'
import type { PickChapter } from '../SourceCompareModal'
import { queueStatusVisual, statusToken } from '../ui/status'
import { StatusDot } from '../ui/StatusDot'
import { TagChip } from '../ui/TagChip'
import { FileQualityBadge } from './FileQualityBadge'
import { HoverTooltip } from './HoverTooltip'
import { LightActionIcon, LightBadge, LightGroup, LightStack, LightSwitch, LightText } from './lightMantine'
import { chapterLabel, readStateOf, type FileOrigin } from './chapterTable'

/** Stable for the page's lifetime (see `useStableActions`), so rows never re-render over a handler. */
export type ChapterRowActions = {
  clickRow: (id: number, shiftKey: boolean) => void
  toggleWanted: (id: number, wanted: boolean) => void
  setRead: (id: number, read: boolean) => void
  link: (id: number) => void
  download: (id: number, label: string) => void
  pickChapter: (pick: PickChapter) => void
  upgradeNow: (id: number) => void
  setTrusted: (fileId: number, trusted: boolean) => void
  deleteFile: (id: number) => void
  toggleSpanFold: (key: string) => void
  setMarkerRef: (key: string, el: HTMLElement | null) => void
}

export type ChapterSpanRowActions = {
  applyWanted: (chapterIds: number[], wanted: boolean) => void
  applyReadState: (chapterIds: number[], state: ChapterReadState) => void
  selectChapters: (chapterIds: number[]) => void
  toggleSpanFold: (key: string) => void
}

type ChapterRowProps = {
  chapter: ChapterDto
  progress: ChapterProgressDto | undefined
  queueItem: QueueItemDto | undefined
  selectMode: boolean
  selected: boolean
  hasAnimeMarkers: boolean
  /** The anime markers at this chapter number, the same array instance across renders. */
  markers: AnimeMarker[] | undefined
  /** Only passed to rows that carry markers, so folding a season doesn't re-render the rest. */
  foldedSpans: ReadonlySet<string> | undefined
  spanForMarker: (num: number, kind: 'start' | 'end') => AnimeSpan | undefined
  canEditMetadata: boolean
  canDownload: boolean
  canDelete: boolean
  canFindBetterCopy: boolean
  wantedPending: boolean
  readPending: boolean
  upgradePending: boolean
  upgradingThis: boolean
  fileOrigin: (name: string, releaseName: string | null) => FileOrigin
  renderLabel: (label: string | MessageDescriptor) => string
  actions: ChapterRowActions
}

export const ChapterRow = memo(function ChapterRow({
  chapter: c,
  progress: rowProgress,
  queueItem,
  selectMode,
  selected: isSelected,
  hasAnimeMarkers,
  markers,
  foldedSpans,
  spanForMarker,
  canEditMetadata,
  canDownload,
  canDelete,
  canFindBetterCopy,
  wantedPending,
  readPending,
  upgradePending,
  upgradingThis,
  fileOrigin,
  renderLabel,
  actions,
}: ChapterRowProps) {
  const { t } = useLingui()
  const chapterLbl = chapterLabel(c)
  const chapterNumber = c.number
  const fileVolume = c.fileVolume
  const { read, inProgress, external, watched } = readStateOf(rowProgress)
  const pageIndex = rowProgress ? rowProgress.pageIndex + 1 : null
  const pageCount = rowProgress ? rowProgress.pageCount : null
  return (
      <Table.Tr
          opacity={c.wanted || c.hasFile ? 1 : 0.55}
          className={[
            watched
                ? 'chapter-row-watched'
                : read
                    ? 'chapter-row-read'
                    : inProgress
                        ? 'chapter-row-reading'
                        : '',
            selectMode ? 'chapter-row-selectable' : '',
            isSelected ? 'chapter-row-selected' : '',
          ]
              .filter(Boolean)
              .join(' ') || undefined}
          onClick={selectMode ? (e) => actions.clickRow(c.id, e.shiftKey) : undefined}
          tabIndex={selectMode ? 0 : undefined}
          onKeyDown={
            selectMode ? onPressKey((e) => actions.clickRow(c.id, e.shiftKey)) : undefined
          }
          aria-selected={selectMode ? isSelected : undefined}
      >
        {/* The controls in this cell stay live in select mode, so its clicks mustn't
            bubble up and toggle the row as well. Same for the actions cell. */}
        <Table.Td onClick={(e) => e.stopPropagation()}>
          <LightSwitch
              checked={c.wanted}
              disabled={!canEditMetadata || wantedPending}
              aria-label={t`Want ${chapterLbl}`}
              onChange={(e) => actions.toggleWanted(c.id, e.currentTarget.checked)}
          />
        </Table.Td>
        {/* The marker slot is reserved on every row, not just the ones carrying a badge:
            the season lines run down that slot, and a label free to grow into it on the
            other 200 rows would have the line drawn straight through it. */}
        <Table.Td className={hasAnimeMarkers ? 'chapter-cell' : undefined} data-chapter-number={c.number ?? undefined}>
          <LightGroup gap={6} wrap="nowrap">
            {c.fileVolume !== null && !c.isOneShot && c.number !== null && (
                <HoverTooltip label={t`Contained in a volume/compilation file`} withArrow>
                  <LightBadge color="var(--watched)" className="tnum chapter-label">
                    <Trans>Vol. {fileVolume}</Trans>
                  </LightBadge>
                </HoverTooltip>
            )}
            <LightText fw={600} className="tnum chapter-label">
              {c.isOneShot || c.number === null
                  ? chapterLabel(c)
                  : c.fileVolume !== null
                      ? <Trans>Ch. {chapterNumber}</Trans>
                      : chapterLabel(c)}
            </LightText>
          </LightGroup>
          {hasAnimeMarkers && c.number !== null && (
              <div className="chapter-span-markers">
                {(markers ?? []).map((marker, i) => {
                  // A marker that starts or ends an included span doubles as its fold
                  // control and as the anchor the overlay measures its line from;
                  // everything else (an unpaired end, or one bumped by the 3-lane cap) is
                  // a plain informational badge, same as always.
                  const span = spanForMarker(c.number!, marker.kind)
                  const folded = span !== undefined && (foldedSpans?.has(span.key) ?? false)
                  const { label: markerLabel } = marker
                  return (
                      <HoverTooltip
                          key={i}
                          label={
                            span
                                ? marker.kind === 'start'
                                    ? folded
                                        ? t`${markerLabel} Anime adaptation starts here · click to expand`
                                        : t`${markerLabel} Anime adaptation starts here · click to collapse`
                                    : folded
                                        ? t`${markerLabel} Anime adaptation ends here · click to expand`
                                        : t`${markerLabel} Anime adaptation ends here · click to collapse`
                                : marker.kind === 'start'
                                    ? t`${markerLabel} Anime adaptation starts here`
                                    : t`${markerLabel} Anime adaptation ends here`
                          }
                          withArrow
                      >
                        <LightBadge
                            color={marker.kind === 'start' ? 'var(--info)' : 'var(--danger)'}
                            className={`chapter-span-marker${span ? ' chapter-span-badge' : ''}`}
                            data-span-fold={span && marker.kind === 'start' ? span.key : undefined}
                            ref={
                              span
                                  ? (el: HTMLDivElement | null) =>
                                      actions.setMarkerRef(`${span.key}:${marker.kind}`, el)
                                  : undefined
                            }
                            {...(span
                                ? pressable((e) => {
                                  e.stopPropagation()
                                  actions.toggleSpanFold(span.key)
                                })
                                : { onClick: (e: { stopPropagation: () => void }) => e.stopPropagation() })}
                        >
                          {marker.label}
                        </LightBadge>
                      </HoverTooltip>
                  )
                })}
              </div>
          )}
        </Table.Td>
        <Table.Td>
          <LightText c="var(--ink-3)" lineClamp={1}>
            {c.title}
          </LightText>
        </Table.Td>
        <Table.Td>
          <LightText c="var(--ink-3)" className="tnum chapter-label">
            {c.releaseDate ? formatCalendarDate(c.releaseDate) : '-'}
          </LightText>
        </Table.Td>
        <Table.Td>
          {/* Where the file on disk actually came from, which is what makes a source
              comparison actionable: the winner is often not what you already have. */}
          {!c.hasFile || !c.fileSourceName ? (
              <LightText c="var(--ink-3)" className="chapter-source-empty">
                -
              </LightText>
          ) : (
              (() => {
                const origin = fileOrigin(c.fileSourceName, c.fileReleaseName)
                return (
                    <LightStack gap={4}>
                      <HoverTooltip label={origin.hint} withArrow disabled={!origin.hint}>
                        <TagChip size="sm">{origin.label}</TagChip>
                      </HoverTooltip>
                      <FileQualityBadge quality={c.fileQuality} />
                    </LightStack>
                )
              })()
          )}
        </Table.Td>
        <Table.Td>
          {/* Wraps rather than clipping: a re-read chapter carries a state, a read mark and a page. */}
          <LightGroup gap={10} wrap="wrap">
            {queueItem ? (
                (() => {
                  const visual = queueStatusVisual(queueItem.status)
                  const failure = queueErrorMessage(queueItem, renderLabel)
                  return (
                      <Tooltip
                        label={failure ?? renderLabel(visual.label)}
                        withArrow
                        disabled={failure === null}
                      >
                        <Group gap={6} wrap="nowrap">
                          {queueItem.pagesTotal > 0 && (
                              <Progress
                                  value={(queueItem.pagesDone / queueItem.pagesTotal) * 100}
                                  w={72}
                                  radius="xl"
                                  animated={queueItem.status === 'Downloading'}
                                  color={queueItem.status === 'Failed' ? 'var(--danger)' : 'var(--neutral)'}
                              />
                          )}
                          <StatusDot
                              tone={statusToken(visual.color)}
                              live={queueItem.status === 'Downloading' || undefined}
                              className="tnum"
                          >
                            {queueItem.pagesTotal > 0
                                ? `${renderLabel(visual.label)} ${queueItem.pagesDone}/${queueItem.pagesTotal}`
                                : renderLabel(visual.label)}
                          </StatusDot>
                        </Group>
                      </Tooltip>
                  )
                })()
            ) : c.hasFile ? (
                <>
                  <StatusDot tone="ok">
                    <Trans>Downloaded</Trans>
                  </StatusDot>
                  {c.fileDeleteDueAt && (() => {
                    const daysLeft = Math.max(
                        0,
                        Math.ceil((Date.parse(c.fileDeleteDueAt) - Date.now()) / 86_400_000),
                    )
                    return (
                        <HoverTooltip label={t`Read file cleanup is on for this series`} withArrow>
                          <span className="chapter-status-note">
                            {daysLeft === 0
                                ? t`File removed soon`
                                : plural(daysLeft, {
                                  one: 'File removed in # day',
                                  other: 'File removed in # days',
                                })}
                          </span>
                        </HoverTooltip>
                    )
                  })()}
                </>
            ) : c.fileRemovedAt ? (
                <HoverTooltip label={t`The file was deleted on purpose. Maki won't download it again unless you ask.`} withArrow>
                  <StatusDot tone="neutral">
                    <Trans>File removed</Trans>
                  </StatusDot>
                </HoverTooltip>
            ) : (
                <StatusDot tone="neutral">
                  <Trans>Missing</Trans>
                </StatusDot>
            )}
            {read && (
                <HoverTooltip
                    label={
                      watched
                          ? t`Marked watched, not read. Doesn't count toward reading stats`
                          : external
                              ? t`Read in Kavita`
                              : t`Read in Maki`
                    }
                    withArrow
                >
                  <span className="chapter-read-mark" data-tone={watched ? 'watched' : 'ok'}>
                    {watched ? <Trans>Watched</Trans> : <Trans>Read</Trans>}
                  </span>
                </HoverTooltip>
            )}
            {/* Shown alongside Read when a finished chapter is being re-read. */}
            {inProgress && (
                <span className="chapter-status-note tnum">
                  {/* pageCount is 0 on rows imported from Kavita: the reader fills it
                      in on first open, so show a plain label until then. */}
                  {rowProgress && rowProgress.pageCount > 0 ? (
                      <Trans>Page {pageIndex}/{pageCount}</Trans>
                  ) : (
                      <Trans>Reading</Trans>
                  )}
                </span>
            )}
          </LightGroup>
        </Table.Td>
        <Table.Td onClick={(e) => e.stopPropagation()}>
          <LightGroup gap={2} wrap="nowrap" justify="flex-end">
            {/* Marking read needs the file for its page count, so a removed chapter can only be un-read. */}
            {(c.hasFile || (c.fileRemovedAt && read)) && (
                <HoverTooltip label={read ? t`Mark unread` : t`Mark read`} withArrow>
                  <LightActionIcon
                      variant={read ? 'light' : 'subtle'}
                      color={read ? 'var(--ok)' : 'gray'}
                      loading={readPending}
                      onClick={() => actions.setRead(c.id, !read)}
                      aria-label={t`Toggle read state of ${chapterLbl}`}
                  >
                    {!read ? <IconEye size={17} /> : <IconEyeOff size={17} />}
                  </LightActionIcon>
                </HoverTooltip>
            )}
            {c.hasFile && (
                <HoverTooltip label={t`Read`} withArrow>
                  <LightActionIcon
                      to={`/read/${c.id}`}
                      variant="subtle"
                      color="brand"
                      aria-label={t`Read ${chapterLbl}`}
                  >
                    <IconBook size={17} />
                  </LightActionIcon>
                </HoverTooltip>
            )}
            {!c.hasFile && canEditMetadata && (
                <HoverTooltip label={t`Link to a file already on disk`} withArrow>
                  <LightActionIcon
                      variant="subtle"
                      color="var(--neutral)"
                      onClick={() => actions.link(c.id)}
                      aria-label={t`Link ${chapterLbl} to a file`}
                  >
                    <IconLink size={17} />
                  </LightActionIcon>
                </HoverTooltip>
            )}
            {!c.hasFile && canDownload && (
                <HoverTooltip label={t`Download this chapter`} withArrow>
                  <LightActionIcon
                      variant="subtle"
                      color="brand"
                      onClick={() => actions.download(c.id, chapterLbl)}
                      aria-label={t`Download ${chapterLbl}`}
                  >
                    <IconDownload size={17} />
                  </LightActionIcon>
                </HoverTooltip>
            )}
            {(canDownload || canDelete) && c.hasFile && (
                <Menu shadow="md" position="bottom-end" withinPortal>
                  <Menu.Target>
                    <ActionIcon
                        variant="subtle"
                        color="var(--neutral)"
                        aria-label={t`More actions for ${chapterLbl}`}
                    >
                      <IconDotsVertical size={17} />
                    </ActionIcon>
                  </Menu.Target>
                  <Menu.Dropdown>
                    {canDownload && c.number !== null && canFindBetterCopy && (
                        <Menu.Item
                            onClick={() =>
                                actions.pickChapter({
                                  id: c.id,
                                  number: c.number!,
                                  label: chapterLbl,
                                  currentSourceName: c.fileSourceName,
                                })
                            }
                        >
                          <Trans>Find better copy</Trans>
                        </Menu.Item>
                    )}
                    {canDownload && (
                    <Menu.Item
                        rightSection={upgradingThis ? <Loader size={14} /> : undefined}
                        disabled={upgradePending}
                        onClick={() => actions.upgradeNow(c.id)}
                    >
                      <Trans>Upgrade now</Trans>
                    </Menu.Item>
                    )}
                    {canDownload && c.fileQuality && (
                        <Menu.Item
                            onClick={() => actions.setTrusted(c.fileQuality!.fileId, !c.fileQuality!.trusted)}
                        >
                          {c.fileQuality.trusted
                              ? <Trans>Allow upgrades</Trans>
                              : <Trans>Protect from upgrades</Trans>}
                        </Menu.Item>
                    )}
                    {canDelete && (
                        <>
                          {canDownload && <Menu.Divider />}
                          <Menu.Item
                              color="var(--danger)"
                              onClick={() => actions.deleteFile(c.id)}
                          >
                            <Trans>Delete file</Trans>
                          </Menu.Item>
                        </>
                    )}
                  </Menu.Dropdown>
                </Menu>
            )}
          </LightGroup>
        </Table.Td>
      </Table.Tr>
  )
})

/** "Ch.1–270", or "Ch.315+" for a season with no end marker yet. */
const spanRangeLabel = (span: AnimeSpan) => {
  const { from, to, openEnded } = span
  return openEnded ? staticT`Ch.${from}+` : staticT`Ch.${from}–${to}`
}

type ChapterSpanRowProps = {
  span: AnimeSpan
  rows: ChapterDto[]
  readProgress: ReadonlyMap<number, ChapterProgressDto>
  hasAnimeMarkers: boolean
  readTracking: boolean
  canEditMetadata: boolean
  wantedPending: boolean
  actions: ChapterSpanRowActions
}

/** The single row a folded span collapses to: the range, what's in it, and what to do with it. */
export const ChapterSpanRow = memo(function ChapterSpanRow({
  span,
  rows,
  readProgress,
  hasAnimeMarkers,
  readTracking,
  canEditMetadata,
  wantedPending,
  actions,
}: ChapterSpanRowProps) {
  const { t } = useLingui()
  const { label: spanLabel } = span
  const total = rows.length
  const downloaded = rows.filter((c) => c.hasFile)
  const downloadedCount = downloaded.length
  const states = downloaded.map((c) => readStateOf(readProgress.get(c.id)))
  const watchedCount = states.filter((st) => st.watched).length
  const readCount = states.filter((st) => st.read && !st.watched).length
  const done = watchedCount + readCount
  const ids = rows.map((c) => c.id)

  // A folded span stands in for every chapter under it, so its switch has to speak for all of
  // them. When they disagree there is no honest on/off to show: the track gets a half-filled
  // look and the thumb a dash, and clicking resolves the whole range to wanted (the same way a
  // tri-state checkbox settles), which is why `checked` is "all of them" and not "any of them".
  const wantedCount = rows.filter((c) => c.wanted).length
  const allWanted = wantedCount === rows.length
  const mixed = wantedCount > 0 && !allWanted

  return (
      <Table.Tr className="chapter-span-row">
        <Table.Td onClick={(e) => e.stopPropagation()}>
          <Tooltip
              label={
                mixed
                    ? plural(total, {
                        one: `${wantedCount} of # chapter wanted · click to want all`,
                        other: `${wantedCount} of # chapters wanted · click to want all`,
                      })
                    : allWanted
                        ? plural(total, { one: 'All # chapter wanted', other: 'All # chapters wanted' })
                        : plural(total, { one: 'None of the # chapter wanted', other: 'None of the # chapters wanted' })
              }
              withArrow
          >
            <Switch
                size="xs"
                checked={allWanted}
                classNames={mixed ? { track: 'chapter-span-wanted-mixed' } : undefined}
                thumbIcon={mixed ? <IconMinus size={10} stroke={3} /> : undefined}
                aria-label={plural(total, {
                  one: `Wanted for ${spanLabel}: ${wantedCount} of # chapter`,
                  other: `Wanted for ${spanLabel}: ${wantedCount} of # chapters`,
                })}
                disabled={!canEditMetadata || wantedPending}
                onChange={(e) => actions.applyWanted(ids, e.currentTarget.checked)}
            />
          </Tooltip>
        </Table.Td>
        <Table.Td className={hasAnimeMarkers ? 'chapter-cell' : undefined}>
          <Group gap={6} wrap="nowrap">
            <Badge
                size="sm"
                color="var(--info)"
                variant="light"
                leftSection={<IconDeviceTv size={12} />}
                className="chapter-span-badge"
                data-span-fold={span.key}
                {...pressable(() => actions.toggleSpanFold(span.key))}
            >
              {span.label}
            </Badge>
            <Text size="sm" fw={600} className="tnum chapter-label">
              {spanRangeLabel(span)}
            </Text>
          </Group>
        </Table.Td>
        <Table.Td>
          <Text size="sm" c="var(--ink-3)" className="tnum">
            {plural(total, {
              one: `# chapter · ${downloadedCount} downloaded`,
              other: `# chapters · ${downloadedCount} downloaded`,
            })}
            {watchedCount > 0 && (
                <>
                  {' · '}
                  <Trans>{watchedCount} watched</Trans>
                </>
            )}
            {readCount > 0 && (
                <>
                  {' · '}
                  <Trans>{readCount} read</Trans>
                </>
            )}
            {/* Spelled out only when the range disagrees with itself: the switch alone can show
              that state but not its size, and hovering for a tooltip is a poor way to find out. */}
            {mixed && (
                <>
                  {' · '}
                  <Trans>{wantedCount} wanted</Trans>
                </>
            )}
          </Text>
        </Table.Td>
        <Table.Td />
        <Table.Td />
        <Table.Td>
          {downloaded.length > 0 && (
              <Progress
                  value={(done / downloaded.length) * 100}
                  color={watchedCount > readCount ? 'var(--watched)' : 'var(--ok)'}
                  size="sm"
                  radius="xl"
              />
          )}
        </Table.Td>
        <Table.Td onClick={(e) => e.stopPropagation()}>
          <Group gap={2} wrap="nowrap" justify="flex-end">
            {readTracking && (
                <Menu shadow="md" position="bottom-end" withinPortal>
                  <Menu.Target>
                    <ActionIcon variant="subtle" color="var(--neutral)" aria-label={t`Actions for ${spanLabel}`}>
                      <IconDotsVertical size={17} />
                    </ActionIcon>
                  </Menu.Target>
                  <Menu.Dropdown>
                    <Menu.Item
                        onClick={() => actions.applyReadState(ids, 'watched')}
                    >
                      <Trans>Mark watched</Trans>
                    </Menu.Item>
                    <Menu.Item
                        onClick={() => actions.applyReadState(ids, 'read')}
                    >
                      <Trans>Mark read</Trans>
                    </Menu.Item>
                    <Menu.Item
                        onClick={() => actions.applyReadState(ids, 'unread')}
                    >
                      <Trans>Mark unread</Trans>
                    </Menu.Item>
                    <Menu.Divider />
                    <Menu.Item
                        onClick={() => actions.selectChapters(ids)}
                    >
                      <Trans>Select these chapters</Trans>
                    </Menu.Item>
                  </Menu.Dropdown>
                </Menu>
            )}
            <Tooltip label={t`Expand`} withArrow>
              <ActionIcon
                  variant="subtle"
                  color="var(--neutral)"
                  onClick={() => actions.toggleSpanFold(span.key)}
                  aria-label={t`Expand ${spanLabel}`}
              >
                <IconChevronDown size={17} />
              </ActionIcon>
            </Tooltip>
          </Group>
        </Table.Td>
      </Table.Tr>
  )
})

/**
 * Stand-in rows while the chapter list loads or the real rows render. One bar per real cell, so the
 * columns keep their widths and the narrow-screen layout, which places cells by position, still applies.
 */
export function ChapterRowSkeletons({ count }: { count: number }) {
  return Array.from({ length: count }, (_, i) => (
      <Table.Tr key={i} aria-hidden>
        <Table.Td><Skeleton h={16} w={30} radius="xl" /></Table.Td>
        <Table.Td><Skeleton h={14} w={72} /></Table.Td>
        <Table.Td><Skeleton h={14} w="60%" /></Table.Td>
        <Table.Td><Skeleton h={14} w={78} /></Table.Td>
        <Table.Td><Skeleton h={18} w={64} radius="sm" /></Table.Td>
        <Table.Td><Skeleton h={18} w={92} radius="sm" /></Table.Td>
        <Table.Td><Skeleton h={18} w={52} ml="auto" /></Table.Td>
      </Table.Tr>
  ))
}

/**
 * Mounts a long list of rows a chunk at a time, so filling the table never blocks for the whole
 * list at once. Only the first fill is chunked: later changes render in one go, and sorting an
 * "All" page only reorders rows that are already mounted, which the row memo makes cheap.
 */
export function ChunkedRows<T>({ rows, chunk = 100, children }: {
  rows: T[]
  chunk?: number
  children: (rows: T[]) => ReactNode
}) {
  const [limit, setLimit] = useState(() => (rows.length > chunk ? chunk : Infinity))
  useEffect(() => {
    if (limit === Infinity) return
    if (limit >= rows.length) setLimit(Infinity)
    else startTransition(() => setLimit(limit + chunk))
  }, [limit, rows.length, chunk])
  return children(limit >= rows.length ? rows : rows.slice(0, limit))
}
