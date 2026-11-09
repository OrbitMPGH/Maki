import { memo, startTransition, useEffect, useState } from 'react'
import type { ReactNode } from 'react'
import { plural, t as staticT } from '@lingui/core/macro'
import { Trans, useLingui } from '@lingui/react/macro'
import type { MessageDescriptor } from '@lingui/core'
import { ActionIcon, Badge, Box, Group, Loader, Menu, Popover, Progress, Skeleton, Switch, Table, Text, Tooltip } from '@mantine/core'
import {
  IconAlertTriangle,
  IconBook,
  IconChevronDown,
  IconCircleCheck,
  IconClock,
  IconDeviceMobileCheck,
  IconDeviceMobileDown,
  IconDeviceMobileOff,
  IconDeviceMobileX,
  IconDeviceTv,
  IconDotsVertical,
  IconDownload,
  IconEye,
  IconEyeCheck,
  IconEyeOff,
  IconLink,
  IconLinkOff,
  IconListCheck,
  IconLock,
  IconLockOpen,
  IconMinus,
  IconNote,
  IconPhotoSearch,
  IconCircleArrowUp,
  IconTrash,
  IconTrashX,
  IconUsers,
} from '../../lib/icons'
import type { NoteCountDto } from '../../api/notes'
import { ChapterNotesDropdown } from '../notes/NotesDropdown'
import { usePhoneSheet } from '../notes/phone'
import { queueErrorMessage } from '../../api/queue'
import type { ChapterProgressDto, ChapterReadState } from '../../api/reader'
import type { ChapterDto, QueueItemDto } from '../../api/types'
import { chapterLabel as houseChapterLabel, chapterRangeLabel, formatCalendarDate } from '../../format'
import type { OfflineChapter } from '../../pwa/contract'
import type { SaveProgress } from '../../pwa/offlineChapters'
import type { AnimeMarker, AnimeSpan } from '../../lib/animeCoverage'
import { onPressKey, pressable } from '../../lib/pressable'
import type { PickChapter } from '../SourceCompareModal'
import { MetaList } from '../ui/MetaList'
import { queueStatusVisual } from '../ui/status'
import { FileQualityBadge } from './FileQualityBadge'
import { HoverTooltip } from './HoverTooltip'
import { LightActionIcon, LightBadge, LightGroup, LightSwitch, LightText } from './lightMantine'
import { chapterLabel, readStateOf, type FileOrigin } from './chapterTable'
import { displayChapterTitle } from '../../lib/chapterTitle'

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
  saveOffline: (id: number) => void
  removeOffline: (id: number) => void
  openNotes: (id: number) => void
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
  /** Notes on this chapter; absent when it has none or the counts are unavailable. */
  noteCounts?: NoteCountDto
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
  /** Why the actions that talk to a source are switched off (an archived series), or null while they work. */
  sourceToolsDisabledReason?: string | null
  /** This browser can keep chapters for offline reading (a secure context with a service worker). */
  canSaveOffline: boolean
  /** This device's copy of the chapter, if it keeps one. */
  offline: OfflineChapter | undefined
  /** Pages stored so far while the copy is being made. */
  offlineSaving: SaveProgress | undefined
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
  noteCounts,
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
  sourceToolsDisabledReason = null,
  canSaveOffline,
  offline,
  offlineSaving,
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
  const fileVolume = c.fileVolume
  const { read, inProgress, external, watched } = readStateOf(rowProgress)
  const pageIndex = rowProgress ? rowProgress.pageIndex + 1 : null
  const pageCount = rowProgress ? rowProgress.pageCount : null
  const removing = c.hasFile && c.fileDeleteDueAt !== null
  return (
      <Table.Tr
          opacity={c.wanted || c.hasFile || c.fileAutoRemoved ? 1 : 0.55}
          className={[
            watched
                ? 'chapter-row-watched'
                : read
                    ? 'chapter-row-read'
                    : inProgress
                        ? 'chapter-row-reading'
                        : '',
            removing ? 'chapter-row-removing' : '',
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
                <span className="chapter-lead-slot">
                  <HoverTooltip label={t`Contained in a volume/compilation file`} withArrow>
                    <LightBadge color="var(--volume)" className="tnum chapter-label">
                      <Trans>Vol. {fileVolume}</Trans>
                    </LightBadge>
                  </HoverTooltip>
                </span>
            )}
            <LightText fw={600} className="tnum chapter-label chapter-number">
              {c.isOneShot || c.number === null
                  ? chapterLabel(c)
                  : c.fileVolume !== null
                      ? houseChapterLabel(c.number)
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
                                        ? t`${markerLabel} Anime adaptation starts here. Click to expand.`
                                        : t`${markerLabel} Anime adaptation starts here. Click to collapse.`
                                    : folded
                                        ? t`${markerLabel} Anime adaptation ends here. Click to expand.`
                                        : t`${markerLabel} Anime adaptation ends here. Click to collapse.`
                                : marker.kind === 'start'
                                    ? t`${markerLabel} Anime adaptation starts here`
                                    : t`${markerLabel} Anime adaptation ends here`
                          }
                          withArrow
                      >
                        <LightBadge
                            color="var(--anime)"
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
            {displayChapterTitle(c)}
          </LightText>
        </Table.Td>
        <Table.Td className="chapter-released">
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
                // Side by side, wrapping only when the column is too narrow: stacked, the two
                // pills made every row about 61px tall for one line of data.
                return (
                    <LightGroup gap={4} wrap="wrap">
                      <HoverTooltip label={origin.hint} withArrow disabled={!origin.hint}>
                        <LightBadge variant={origin.scraped ? 'light' : 'outline'} color="var(--neutral)">
                          {origin.label}
                        </LightBadge>
                      </HoverTooltip>
                      <FileQualityBadge quality={c.fileQuality} />
                    </LightGroup>
                )
              })()
          )}
        </Table.Td>
        <Table.Td>
          {/* Wraps rather than clipping: a re-read chapter carries three badges. */}
          <LightGroup gap={6} wrap="wrap">
            {queueItem ? (
                (() => {
                  const visual = queueStatusVisual(queueItem.status)
                  const failure = queueErrorMessage(queueItem, renderLabel)
                  const queueTone = visual.color
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
                                  color={queueTone}
                                  aria-label={t`Download progress`}
                              />
                          )}
                          <Badge
                              size="sm"
                              color={queueTone}
                              variant="light"
                              leftSection={<visual.Icon size={12} />}
                              className="tnum"
                          >
                            {queueItem.pagesTotal > 0
                                ? `${renderLabel(visual.label)} ${queueItem.pagesDone}/${queueItem.pagesTotal}`
                                : renderLabel(visual.label)}
                          </Badge>
                        </Group>
                      </Tooltip>
                  )
                })()
            ) : c.hasFile && c.fileMissing ? (
                <HoverTooltip
                    label={t`The last health scan couldn't find this file on disk. Relink files finds it if it was moved or renamed.`}
                    withArrow
                    multiline
                    w={280}
                >
                  <LightBadge color="var(--warn)" leftSection={<IconAlertTriangle size={12} />}>
                    <Trans>File missing</Trans>
                  </LightBadge>
                </HoverTooltip>
            ) : c.hasFile ? (
                <>
                  <LightBadge color="var(--ok)" leftSection={<IconCircleCheck size={12} />}>
                    <Trans>Downloaded</Trans>
                  </LightBadge>
                  {c.fileDeleteDueAt && (() => {
                    const daysLeft = Math.max(
                        0,
                        Math.ceil((Date.parse(c.fileDeleteDueAt) - Date.now()) / 86_400_000),
                    )
                    const dueOn = formatCalendarDate(c.fileDeleteDueAt)
                    return (
                        <HoverTooltip
                            label={t`Everyone has read this chapter, so read file cleanup deletes the file around ${dueOn}. It gets no upgrades until then.`}
                            withArrow
                            multiline
                            w={280}
                        >
                          <LightBadge color="var(--warn)" leftSection={<IconClock size={12} />}>
                            {daysLeft === 0
                                ? t`Removal pending`
                                : plural(daysLeft, {
                                  one: 'Removed in # day',
                                  other: 'Removed in # days',
                                })}
                          </LightBadge>
                        </HoverTooltip>
                    )
                  })()}
                </>
            ) : c.fileRemovedAt && c.fileAutoRemoved ? (
                (() => {
                  const removedOn = formatCalendarDate(c.fileRemovedAt)
                  return (
                      <HoverTooltip
                          label={t`Read file cleanup deleted the file on ${removedOn} after everyone read it. Turn Wanted back on to download it again.`}
                          withArrow
                          multiline
                          w={280}
                      >
                        <LightBadge color="var(--neutral)" leftSection={<IconTrashX size={12} />}>
                          <Trans>Auto-removed</Trans>
                        </LightBadge>
                      </HoverTooltip>
                  )
                })()
            ) : c.fileRemovedAt ? (
                <HoverTooltip label={t`The file was deleted on purpose. Maki won't download it again unless you ask.`} withArrow>
                  <LightBadge color="var(--neutral)" leftSection={<IconTrash size={12} />}>
                    <Trans>File removed</Trans>
                  </LightBadge>
                </HoverTooltip>
            ) : (
                <LightBadge color="var(--neutral)">
                  <Trans>Not downloaded</Trans>
                </LightBadge>
            )}
            {c.delistedAt && (() => {
              const delistedOn = formatCalendarDate(c.delistedAt)
              return (
                  <HoverTooltip
                      label={t`No source has listed this chapter since ${delistedOn}. A source may have filed it here by mistake, so check the file. Sources also drop chapters after a licence takedown, and then the file is fine.`}
                      withArrow
                      multiline
                      w={280}
                  >
                    <LightBadge
                        color={c.hasFile ? 'var(--warn)' : 'var(--neutral)'}
                        leftSection={<IconLinkOff size={12} />}
                    >
                      <Trans>Delisted</Trans>
                    </LightBadge>
                  </HoverTooltip>
              )
            })()}
            {offline && <OfflineBadge record={offline} saving={offlineSaving} />}
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
                  <LightBadge
                      color={watched ? 'var(--anime)' : 'var(--brand)'}
                      variant={watched || external ? 'light' : 'filled'}
                      leftSection={
                        watched ? <IconDeviceTv size={12} /> : <IconEyeCheck size={12} />
                      }
                  >
                    {watched ? <Trans>Watched</Trans> : <Trans>Read</Trans>}
                  </LightBadge>
                </HoverTooltip>
            )}
            {/* Shown alongside Read when a finished chapter is being re-read. */}
            {inProgress && (
                <LightBadge color="var(--brand)" className="tnum">
                  {/* pageCount is 0 on rows imported from Kavita: the reader fills it
                      in on first open, so show a plain label until then. */}
                  {rowProgress && rowProgress.pageCount > 0 ? (
                      <Trans>Page {pageIndex}/{pageCount}</Trans>
                  ) : (
                      <Trans>Reading</Trans>
                  )}
                </LightBadge>
            )}
          </LightGroup>
        </Table.Td>
        <Table.Td onClick={(e) => e.stopPropagation()}>
          <LightGroup gap={2} wrap="nowrap" justify="flex-end">
            {noteCounts && (noteCounts.mine > 0 || noteCounts.shared > 0) && (
                <ChapterNotesCount
                    chapter={c}
                    counts={noteCounts}
                    name={chapterLbl}
                    spoilerGuard={!(read || watched)}
                    onOpenSheet={actions.openNotes}
                />
            )}
            {/* Marking read needs the file for its page count, so without one the button only clears progress. */}
            {c.hasFile ? (
                <HoverTooltip label={read ? t`Mark unread` : t`Mark read`} withArrow>
                  <LightActionIcon
                      variant={read ? 'light' : 'subtle'}
                      color={read ? 'var(--brand)' : 'gray'}
                      data-row-action={read ? undefined : ''}
                      loading={readPending}
                      onClick={() => actions.setRead(c.id, !read)}
                      aria-label={t`Toggle read state of ${chapterLbl}`}
                  >
                    {!read ? <IconEye size={19} /> : <IconEyeOff size={19} />}
                  </LightActionIcon>
                </HoverTooltip>
            ) : (read || inProgress) && (
                <HoverTooltip label={read ? t`Mark unread` : t`Clear reading progress`} withArrow>
                  <LightActionIcon
                      variant="light"
                      color="var(--brand)"
                      loading={readPending}
                      onClick={() => actions.setRead(c.id, false)}
                      aria-label={read ? t`Mark ${chapterLbl} unread` : t`Clear reading progress of ${chapterLbl}`}
                  >
                    <IconEyeOff size={19} />
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
                    <IconBook size={19} />
                  </LightActionIcon>
                </HoverTooltip>
            )}
            {!c.hasFile && canEditMetadata && (
                <HoverTooltip label={t`Link to a file already on disk`} withArrow>
                  <LightActionIcon
                      variant="subtle"
                      color="var(--neutral)"
                      data-row-action=""
                      onClick={() => actions.link(c.id)}
                      aria-label={t`Link ${chapterLbl} to a file`}
                  >
                    <IconLink size={19} />
                  </LightActionIcon>
                </HoverTooltip>
            )}
            {!c.hasFile && canDownload && (
                <HoverTooltip label={sourceToolsDisabledReason ?? t`Download this chapter`} withArrow>
                  <LightActionIcon
                      variant="subtle"
                      color="brand"
                      data-disabled={sourceToolsDisabledReason !== null || undefined}
                      aria-disabled={sourceToolsDisabledReason !== null || undefined}
                      onClick={() => {
                        if (sourceToolsDisabledReason === null) actions.download(c.id, chapterLbl)
                      }}
                      aria-label={t`Download ${chapterLbl}`}
                  >
                    <IconDownload size={19} />
                  </LightActionIcon>
                </HoverTooltip>
            )}
            {(canDownload || canDelete || canSaveOffline) && c.hasFile && (
                <Menu shadow="md" position="bottom-end" withinPortal>
                  <Menu.Target>
                    <ActionIcon
                        variant="subtle"
                        color="var(--neutral)"
                        data-row-action=""
                        aria-label={t`More actions for ${chapterLbl}`}
                    >
                      <IconDotsVertical size={19} />
                    </ActionIcon>
                  </Menu.Target>
                  <Menu.Dropdown>
                    {canSaveOffline && (!offline || offline.state === 'error') && (
                        <Menu.Item
                            leftSection={<IconDeviceMobileDown size={16} />}
                            onClick={() => actions.saveOffline(c.id)}
                        >
                          <Trans>Save for offline</Trans>
                        </Menu.Item>
                    )}
                    {canSaveOffline && offline && (
                        <Menu.Item
                            leftSection={<IconDeviceMobileOff size={16} />}
                            onClick={() => actions.removeOffline(c.id)}
                        >
                          <Trans>Remove from this device</Trans>
                        </Menu.Item>
                    )}
                    {canSaveOffline && (canDownload || canDelete) && <Menu.Divider />}
                    {canDownload && c.number !== null && canFindBetterCopy && !removing && (
                        <Tooltip label={sourceToolsDisabledReason} position="left" withArrow disabled={sourceToolsDisabledReason === null} events={{ hover: true, focus: true, touch: true }}>
                        <Menu.Item
                            leftSection={<IconPhotoSearch size={16} />}
                            data-disabled={sourceToolsDisabledReason !== null || undefined}
                            aria-disabled={sourceToolsDisabledReason !== null || undefined}
                            closeMenuOnClick={sourceToolsDisabledReason === null}
                            onClick={() => {
                              if (sourceToolsDisabledReason !== null) return
                              actions.pickChapter({
                                id: c.id,
                                number: c.number!,
                                label: chapterLbl,
                                currentSourceName: c.fileSourceName,
                              })
                            }}
                        >
                          <Trans>Find better copy</Trans>
                        </Menu.Item>
                        </Tooltip>
                    )}
                    {canDownload && !removing && (
                    <Tooltip label={sourceToolsDisabledReason} position="left" withArrow disabled={sourceToolsDisabledReason === null} events={{ hover: true, focus: true, touch: true }}>
                    <Menu.Item
                        leftSection={upgradingThis ? <Loader size={16} /> : <IconCircleArrowUp size={16} />}
                        disabled={upgradePending}
                        data-disabled={sourceToolsDisabledReason !== null || undefined}
                        aria-disabled={sourceToolsDisabledReason !== null || undefined}
                        closeMenuOnClick={sourceToolsDisabledReason === null}
                        onClick={() => {
                          if (sourceToolsDisabledReason === null) actions.upgradeNow(c.id)
                        }}
                    >
                      <Trans>Upgrade now</Trans>
                    </Menu.Item>
                    </Tooltip>
                    )}
                    {canDownload && c.fileQuality && (
                        <Menu.Item
                            leftSection={
                              c.fileQuality.trusted
                                  ? <IconLockOpen size={16} />
                                  : <IconLock size={16} />
                            }
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
                              leftSection={<IconTrash size={16} />}
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

/** This device's copy of a chapter: being saved, saved, or failed. */
function OfflineBadge({ record, saving }: { record: OfflineChapter; saving: SaveProgress | undefined }) {
  const { t } = useLingui()
  if (record.state === 'error') {
    return (
        <HoverTooltip label={t`Saving for offline failed. Try again from the chapter's menu.`} withArrow>
          <LightBadge color="var(--danger)" leftSection={<IconDeviceMobileX size={12} />}>
            <Trans>Not saved</Trans>
          </LightBadge>
        </HoverTooltip>
    )
  }
  if (record.state === 'saving' || record.state === 'stale') {
    const done = saving?.done ?? 0
    const total = saving?.total ?? 0
    return (
        <HoverTooltip label={t`Saving on this device for offline reading`} withArrow>
          <LightBadge color="var(--info)" className="tnum" leftSection={<IconDeviceMobileDown size={12} />}>
            {total > 0 ? <Trans>Saving {done}/{total}</Trans> : <Trans>Saving</Trans>}
          </LightBadge>
        </HoverTooltip>
    )
  }
  return (
      <HoverTooltip label={t`Saved on this device for offline reading`} withArrow>
        <LightBadge color="var(--info)" leftSection={<IconDeviceMobileCheck size={12} />}>
          <Trans>On this device</Trans>
        </LightBadge>
      </HoverTooltip>
  )
}

/** "Ch. 1–270", or "Ch. 315+" for a season with no end marker yet. */
const spanRangeLabel = (span: AnimeSpan) => {
  const { from, to, openEnded } = span
  return openEnded ? staticT`Ch. ${from}+` : chapterRangeLabel(from, to)
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
  const downloadedCount = downloaded.filter((c) => !c.fileMissing).length
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
                        one: `${wantedCount} of # chapter wanted. Click to want all.`,
                        other: `${wantedCount} of # chapters wanted. Click to want all.`,
                      })
                    : allWanted
                        ? plural(total, { one: 'All # chapter wanted', other: 'All # chapters wanted' })
                        : plural(total, { one: 'None of the # chapter wanted', other: 'None of the # chapters wanted' })
              }
              withArrow
          >
            <Switch
                size="sm"
                checked={allWanted}
                classNames={mixed ? { track: 'chapter-span-wanted-mixed' } : undefined}
                thumbIcon={mixed ? <IconMinus size={10} /> : undefined}
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
            <span className="chapter-lead-slot">
              <Badge
                  size="sm"
                  color="var(--anime)"
                  variant="light"
                  leftSection={<IconDeviceTv size={12} />}
                  className="chapter-span-badge"
                  data-span-fold={span.key}
                  {...pressable(() => actions.toggleSpanFold(span.key))}
              >
                {span.label}
              </Badge>
            </span>
            <Text size="sm" fw={600} className="tnum chapter-label chapter-number">
              {spanRangeLabel(span)}
            </Text>
          </Group>
        </Table.Td>
        <Table.Td>
          {/* Wanted is spelled out only when the range disagrees with itself: the switch alone can
            show that state but not its size, and hovering for a tooltip is a poor way to find out. */}
          <MetaList
              className="tnum"
              style={{ color: 'var(--ink-3)', fontSize: 'var(--mantine-font-size-sm)' }}
              items={[
                plural(total, { one: '# chapter', other: '# chapters' }),
                t`${downloadedCount} downloaded`,
                watchedCount > 0 && <Trans>{watchedCount} watched</Trans>,
                readCount > 0 && <Trans>{readCount} read</Trans>,
                mixed && <Trans>{wantedCount} wanted</Trans>,
              ]}
          />
        </Table.Td>
        <Table.Td />
        <Table.Td />
        <Table.Td>
          {downloaded.length > 0 && (
              <div>
                <Text size="xs" fw={600} c="var(--ink-3)" mb={4}>
                  {watchedCount > 0 && readCount === 0
                      ? <Trans>Watched in the anime</Trans>
                      : readCount > 0 && watchedCount === 0
                          ? <Trans>Read</Trans>
                          : <Trans>Read or watched</Trans>}
                </Text>
                <Progress
                    value={(done / downloaded.length) * 100}
                    color={watchedCount > readCount ? 'var(--anime)' : 'var(--brand)'}
                    size="sm"
                    radius="xl"
                    aria-label={t`Reading progress`}
                />
              </div>
          )}
        </Table.Td>
        <Table.Td onClick={(e) => e.stopPropagation()}>
          <Group gap={2} wrap="nowrap" justify="flex-end">
            {/* The menu goes last, in the chapter rows' menu column; without one the chevron keeps its slot. */}
            <Tooltip label={t`Expand`} withArrow>
              <ActionIcon
                  variant="subtle"
                  color="var(--neutral)"
                  data-row-action=""
                  onClick={() => actions.toggleSpanFold(span.key)}
                  aria-label={t`Expand ${spanLabel}`}
              >
                <IconChevronDown size={19} />
              </ActionIcon>
            </Tooltip>
            {readTracking ? (
                <Menu shadow="md" position="bottom-end" withinPortal>
                  <Menu.Target>
                    <ActionIcon variant="subtle" color="var(--neutral)" data-row-action="" aria-label={t`Actions for ${spanLabel}`}>
                      <IconDotsVertical size={19} />
                    </ActionIcon>
                  </Menu.Target>
                  <Menu.Dropdown>
                    <Menu.Item
                        leftSection={<IconDeviceTv size={16} />}
                        onClick={() => actions.applyReadState(ids, 'watched')}
                    >
                      <Trans>Mark watched</Trans>
                    </Menu.Item>
                    <Menu.Item
                        leftSection={<IconEyeCheck size={16} />}
                        onClick={() => actions.applyReadState(ids, 'read')}
                    >
                      <Trans>Mark read</Trans>
                    </Menu.Item>
                    <Menu.Item
                        leftSection={<IconEyeOff size={16} />}
                        onClick={() => actions.applyReadState(ids, 'unread')}
                    >
                      <Trans>Mark unread</Trans>
                    </Menu.Item>
                    <Menu.Divider />
                    <Menu.Item
                        leftSection={<IconListCheck size={16} />}
                        onClick={() => actions.selectChapters(ids)}
                    >
                      <Trans>Select these chapters</Trans>
                    </Menu.Item>
                  </Menu.Dropdown>
                </Menu>
            ) : (
                <Box w={32} style={{ flex: 'none' }} aria-hidden />
            )}
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
        <Table.Td className="chapter-released"><Skeleton h={14} w={78} ml="auto" /></Table.Td>
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

/** The row's note count. It opens the notes in a dropdown under itself, or the bottom sheet on a phone. */
function ChapterNotesCount({
  chapter,
  counts,
  name,
  spoilerGuard,
  onOpenSheet,
}: {
  chapter: ChapterDto
  counts: NoteCountDto
  name: string
  spoilerGuard: boolean
  onOpenSheet: (id: number) => void
}) {
  const { t } = useLingui()
  const phone = usePhoneSheet()
  const [opened, setOpened] = useState(false)
  const { mine, shared } = counts
  const label = t`Notes on ${name}: ${mine} yours, ${shared} shared`
  const button = (
    <button
      type="button"
      className="chapter-notes-button"
      aria-label={label}
      onClick={() => (phone ? onOpenSheet(chapter.id) : setOpened((open) => !open))}
    >
      {mine > 0 && (
        <span className="chapter-notes-count" data-kind="mine">
          <IconNote size={15} />
          {mine}
        </span>
      )}
      {shared > 0 && (
        <span className="chapter-notes-count" data-kind="shared">
          <IconUsers size={15} />
          {shared}
        </span>
      )}
    </button>
  )
  if (phone) {
    return (
      <HoverTooltip label={label} withArrow>
        {button}
      </HoverTooltip>
    )
  }
  return (
    <Popover opened={opened} onChange={setOpened} width={460} position="bottom-end" offset={4} shadow="lg" returnFocus>
      <HoverTooltip label={label} withArrow>
        <Popover.Target>{button}</Popover.Target>
      </HoverTooltip>
      {opened && (
        <ChapterNotesDropdown
          chapterId={chapter.id}
          seriesId={chapter.seriesId}
          chapterLabel={name}
          spoilerGuard={spoilerGuard}
          onClose={() => setOpened(false)}
        />
      )}
    </Popover>
  )
}
