import { ActionIcon, Divider, Group, Progress, Text, Tooltip } from '@mantine/core'
import {
  IconArrowLeft,
  IconBookmark,
  IconBookmarkFilled,
  IconChevronLeft,
  IconChevronRight,
  IconEyeOff,
  IconKeyboard,
  IconLayoutGrid,
  IconMaximize,
  IconMinimize,
  IconMinus,
  IconNote,
  IconPlayerPause,
  IconPlayerPlay,
  IconPlus,
  IconSquareHalf,
  IconZoomIn,
  IconZoomOut,
  IconZoomReset,
} from '../../lib/icons'
import { useEffect, useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'
import type { PrefsSource, ReaderManifest } from '../../api/reader'
import type { ReadingProfile } from '../../api/readingProfiles'
import type { PrefsSelection, ReaderPrefs } from './prefs'
import { setCompareHeld, useCompareHeld } from './compareHold'
import { useEnhanceEngine } from './enhanceRuntime'
import PageScrubber from './PageScrubber'
import ReaderSettings, { OVERLAY_Z } from './ReaderSettings'
import { isPendingPhase, useEnhancePhaseLabel, type EnhancePhase } from './ServerEnhanceStatus'
import { serverChapterLabel } from '../../format'
import { AUTO_SCROLL_MAX, AUTO_SCROLL_MIN } from './useAutoScroll'

export interface AutoScrollControls {
  on: boolean
  level: number
  onToggle: () => void
  onLevel: (level: number) => void
}

export default function ReaderToolbar({
  manifest,
  page,
  pageLabel,
  onSeek,
  onPrevChapter,
  onNextChapter,
  prefs,
  onPrefs,
  autoLayout = false,
  selection,
  onSelection,
  source,
  autoProfileId,
  profiles,
  fullscreen,
  onToggleFullscreen,
  zoom,
  zoomMax,
  onZoomIn,
  onZoomOut,
  onZoomReset,
  incognito,
  onIncognito,
  readingCounted,
  bookmarked,
  bookmarkDisabled,
  onToggleBookmark,
  noteCount,
  notesOpen,
  onToggleNotes,
  stripOpen,
  onToggleStrip,
  visible,
  onHold,
  onReveal,
  onShortcuts,
  serverEnhancing = false,
  enhancedPages,
  upscalingPages,
  enhancePhase = null,
  thumbs = [],
  shownTo,
  autoScroll,
}: {
  manifest: ReaderManifest
  page: number
  /** The page counter's text: one number, or a range when a double-page spread is up. */
  pageLabel: string
  onSeek: (page: number) => void
  onPrevChapter: () => void
  onNextChapter: () => void
  prefs: ReaderPrefs
  onPrefs: (patch: Partial<ReaderPrefs>) => void
  /** The layout on screen was picked by webtoon detection, not by any setting. */
  autoLayout?: boolean
  selection: PrefsSelection
  onSelection: (selection: PrefsSelection) => void
  source: PrefsSource
  autoProfileId: number | null
  profiles: ReadingProfile[]
  fullscreen: boolean
  onToggleFullscreen: () => void
  zoom: number
  zoomMax: number
  onZoomIn: () => void
  onZoomOut: () => void
  onZoomReset: () => void
  incognito: boolean
  onIncognito: (value: boolean) => void
  /** Whether this chapter is being counted as read on screen; ReaderPage owns the rule. */
  readingCounted: boolean
  bookmarked: boolean
  /** Offline: a toggle cannot be queued and replayed safely. */
  bookmarkDisabled: boolean
  onToggleBookmark: () => void
  /** Visible notes on this chapter, from the manifest. */
  noteCount: number
  notesOpen: boolean
  onToggleNotes: () => void
  stripOpen: boolean
  onToggleStrip: () => void
  visible: boolean
  /** Keeps the auto-hide from pulling the chrome out from under an open menu or the cursor. */
  onHold: (held: boolean) => void
  /** Brings the chrome up; called when a keyboard user tabs onto a control while it is hidden. */
  onReveal: () => void
  onShortcuts: () => void
  /** A server method is in use, so pages can show the server's upscale even without the browser engine. */
  serverEnhancing?: boolean
  /** Pages the server has upscaled; the ones ahead are marked on the slider like a video's buffer. */
  enhancedPages?: ReadonlySet<number>
  /** Pages the server is upscaling right now, pulsing on the slider. */
  upscalingPages?: ReadonlySet<number>
  /** Where the server upscale of the pages on screen stands, shown on the compare button. */
  enhancePhase?: EnhancePhase | null
  /** Thumbnail URL per page, shown above the slider under the pointer. */
  thumbs?: string[]
  /** The last page on screen, past `page` when a spread shows two. */
  shownTo?: number
  /** Continuous mode's auto-scroll, which takes the zoom buttons' place there. */
  autoScroll?: AutoScrollControls
}) {
  const { t } = useLingui()

  // The slider and the chapter chevrons mirror in RTL: the left chevron goes to the next chapter,
  // so each chevron's disabled state follows the chapter it actually opens.
  const rtl = prefs.direction === 'rtl'
  const [settingsOpen, setSettingsOpen] = useState(false)

  const enhanceOn = prefs.enhance !== 'off'
  const engineStatus = useEnhanceEngine(enhanceOn).status
  const compareHeld = useCompareHeld()
  const phaseLabel = useEnhancePhaseLabel()
  const phaseShort = enhancePhase
    ? {
        queued: t`Waiting`,
        working: t`Upscaling`,
        loading: t`Loading`,
        done: t`Upscaled`,
        failed: t`Not upscaled`,
      }[enhancePhase]
    : null

  // Shared between the tooltip and the icon button's own aria-label, which say the same thing.
  const backChapterLabel = rtl ? t`Next chapter` : t`Previous chapter`
  const forwardChapterLabel = rtl ? t`Previous chapter` : t`Next chapter`

  // The cursor over a bar, keyboard focus inside one, or the settings popover all keep it up.
  const [hovered, setHovered] = useState(false)
  const [focused, setFocused] = useState(false)
  useEffect(() => {
    onHold(hovered || focused || settingsOpen)
  }, [hovered, focused, settingsOpen, onHold])

  // Only keyboard focus counts: a button clicked with the mouse keeps focus, and holding the chrome
  // for that would stop it ever auto-hiding again.
  const barFocus = {
    onFocus: (event: React.FocusEvent) => {
      if (!(event.target as HTMLElement).matches(':focus-visible')) return
      setFocused(true)
      onReveal()
    },
    onBlur: (event: React.FocusEvent<HTMLElement>) => {
      if (!event.currentTarget.contains(event.relatedTarget as Node | null)) setFocused(false)
    },
  }

  // How much of the series is left, on the same footing as the series page: downloaded chapters as
  // the denominator, completed ones as the numerator.
  const chaptersRead = Math.min(
    manifest.seriesChapterCount,
    manifest.seriesReadCount + (readingCounted ? 1 : 0),
  )
  // Only worth saying when the series is longer than what's on disk; otherwise it just repeats the
  // denominator next to it.
  const moreInSeries = manifest.seriesWantedCount > manifest.seriesChapterCount
  const { seriesChapterCount, seriesWantedCount } = manifest
  const seriesProgressTooltip = moreInSeries
    ? t`${chaptersRead} of ${seriesChapterCount} downloaded chapters read, ${seriesWantedCount} in the series`
    : t`${chaptersRead} of ${seriesChapterCount} downloaded chapters read`

  const notedPages = useMemo(() => new Set(manifest.notedPages ?? []), [manifest.notedPages])

  // Clicks on the bars must not fall through to the page-turn zones behind them.
  const stop = (event: React.MouseEvent) => event.stopPropagation()

  return (
    <>
      <div
        className="reader-bar reader-bar-top"
        data-visible={visible}
        onClick={stop}
        onMouseEnter={() => setHovered(true)}
        onMouseLeave={() => setHovered(false)}
        {...barFocus}
      >
        <Group gap="sm" wrap="nowrap" px="md" h="100%">
          <ActionIcon
            component={Link}
            to={`/series/${manifest.seriesId}`}
            variant="subtle"
            color="gray"
            aria-label={t`Back to series`}
          >
            <IconArrowLeft size={20} />
          </ActionIcon>
          <div style={{ minWidth: 0, flex: 1 }}>
            <Text fz="var(--type-body)" fw={600} truncate>
              {manifest.seriesTitle}
            </Text>
            <Group gap={8} wrap="nowrap" align="center">
              <Text fz="xs" c="var(--ink-3)" style={{ whiteSpace: 'nowrap' }}>
                {serverChapterLabel(manifest.label)}
              </Text>
              {manifest.seriesChapterCount > 0 && <Divider orientation="vertical" />}
              {manifest.seriesChapterCount > 0 && (
                <Tooltip label={seriesProgressTooltip} withArrow zIndex={OVERLAY_Z}>
                  {/* The series meter, not the page one: the bottom bar's slider is this chapter. */}
                  <Group gap={6} wrap="nowrap" align="center" style={{ minWidth: 0 }}>
                    <Text fz="xs" c="var(--ink-2)" fw={600} hiddenFrom="sm" style={{ whiteSpace: 'nowrap' }}>
                      <Trans>Series</Trans>
                    </Text>
                    <Text fz="xs" c="var(--ink-2)" fw={600} visibleFrom="sm" style={{ whiteSpace: 'nowrap' }}>
                      <Trans>Series progress</Trans>
                    </Text>
                    <Progress
                      value={(chaptersRead / manifest.seriesChapterCount) * 100}
                      color="var(--brand)"
                      radius="xl"
                      size="xs"
                      w={64}
                      style={{ flexShrink: 0 }}
                      aria-label={t`Chapters read in this series`}
                    />
                    <Text fz="xs" c="var(--ink-3)" hiddenFrom="xs" className="tnum" style={{ whiteSpace: 'nowrap' }}>
                      {t`${chaptersRead}/${seriesChapterCount}`}
                    </Text>
                    <Text fz="xs" c="var(--ink-3)" visibleFrom="xs" truncate className="tnum">
                      {chaptersRead < seriesChapterCount ? (
                        plural(seriesChapterCount, {
                          one: `${chaptersRead} of # read`,
                          other: `${chaptersRead} of # read`,
                        })
                      ) : (
                        <Plural value={seriesChapterCount} one="all # read" other="all # read" />
                      )}
                      {moreInSeries && (
                        <>
                          {', '}
                          <Plural value={seriesWantedCount} one="# in series" other="# in series" />
                        </>
                      )}
                    </Text>
                  </Group>
                </Tooltip>
              )}
            </Group>
          </div>
          {enhanceOn && (serverEnhancing || (engineStatus !== 'unsupported' && engineStatus !== 'broken')) && (
            <Group gap={6} wrap="nowrap" className="reader-compare" data-phase={enhancePhase ?? undefined}>
              {phaseShort && (
                <Text visibleFrom="sm" fz="xs" c="var(--ink-3)" className="reader-compare-phase" aria-hidden>
                  {phaseShort}
                </Text>
              )}
              <Tooltip
                label={
                  enhancePhase ? (
                    <>
                      <div>{phaseLabel(enhancePhase)}</div>
                      <div>{t`Hold to compare with the original (H)`}</div>
                    </>
                  ) : (
                    t`Hold to compare with the original (H)`
                  )
                }
                withArrow
                zIndex={OVERLAY_Z}
              >
                <ActionIcon
                  className="reader-compare-button"
                  data-busy={isPendingPhase(enhancePhase) || undefined}
                  variant={compareHeld ? 'light' : 'subtle'}
                  color="gray"
                  aria-label={t`Hold to compare`}
                  aria-pressed={compareHeld}
                  style={{ touchAction: 'none', userSelect: 'none', WebkitTouchCallout: 'none' }}
                  onPointerDown={(event) => {
                    event.currentTarget.setPointerCapture?.(event.pointerId)
                    setCompareHeld(true)
                  }}
                  onPointerUp={() => setCompareHeld(false)}
                  onPointerCancel={() => setCompareHeld(false)}
                  onLostPointerCapture={() => setCompareHeld(false)}
                  onPointerLeave={() => setCompareHeld(false)}
                  onKeyDown={(event) => {
                    if ((event.key === ' ' || event.key === 'Enter') && !event.repeat) {
                      event.preventDefault()
                      setCompareHeld(true)
                    }
                  }}
                  onKeyUp={(event) => {
                    if (event.key === ' ' || event.key === 'Enter') setCompareHeld(false)
                  }}
                  onBlur={() => setCompareHeld(false)}
                  onContextMenu={(event) => event.preventDefault()}
                >
                  <IconSquareHalf size={20} />
                </ActionIcon>
              </Tooltip>
            </Group>
          )}
          {incognito && (
            <Tooltip label={t`Incognito, this session isn't being recorded`} withArrow zIndex={OVERLAY_Z}>
              <IconEyeOff size={20} opacity={0.7} />
            </Tooltip>
          )}
          <Tooltip label={t`Notes`} withArrow zIndex={OVERLAY_Z}>
            <ActionIcon
              variant={notesOpen ? 'light' : 'subtle'}
              color="gray"
              w="auto"
              miw={28}
              px={noteCount > 0 ? 6 : 0}
              onClick={onToggleNotes}
              aria-label={noteCount > 0 ? t`Notes (${noteCount})` : t`Notes`}
              aria-expanded={notesOpen}
            >
              <IconNote size={18} />
              {noteCount > 0 && (
                <span className="tnum" style={{ marginInlineStart: 4, fontSize: 12 }}>
                  {noteCount}
                </span>
              )}
            </ActionIcon>
          </Tooltip>
          <Tooltip
            label={
              bookmarkDisabled
                ? t`Bookmarks need a connection`
                : bookmarked
                  ? t`Remove bookmark`
                  : t`Bookmark this page`
            }
            withArrow
            zIndex={OVERLAY_Z}
          >
            <ActionIcon
              variant="subtle"
              color={bookmarked ? 'var(--brand)' : 'gray'}
              onClick={onToggleBookmark}
              disabled={bookmarkDisabled}
              aria-label={t`Toggle bookmark`}
            >
              {bookmarked ? <IconBookmarkFilled size={20} /> : <IconBookmark size={20} />}
            </ActionIcon>
          </Tooltip>
        </Group>
      </div>

      <div
        className="reader-bar reader-bar-bottom"
        data-visible={visible}
        onClick={stop}
        onMouseEnter={() => setHovered(true)}
        onMouseLeave={() => setHovered(false)}
        {...barFocus}
      >
        <Group gap="var(--reader-bar-gap)" wrap="nowrap" px="md" h="100%">
          <Tooltip label={backChapterLabel} withArrow zIndex={OVERLAY_Z}>
            <ActionIcon
              variant="subtle"
              color="gray"
              onClick={rtl ? onNextChapter : onPrevChapter}
              disabled={(rtl ? manifest.nextChapterId : manifest.previousChapterId) === null}
              aria-label={backChapterLabel}
            >
              <IconChevronLeft size={20} />
            </ActionIcon>
          </Tooltip>

          <PageScrubber
            count={manifest.pageCount}
            page={page}
            shownTo={shownTo ?? page}
            rtl={rtl}
            onSeek={onSeek}
            enhanced={enhancedPages}
            upscaling={upscalingPages}
            noted={notedPages}
            thumbs={thumbs}
          />

          {/* Dropped on a phone only when the zoom pair is up: seven controls plus this counter
              leave the slider almost no track. */}
          <Text
            visibleFrom={prefs.mode !== 'vertical' ? 'xs' : undefined}
            fz="xs"
            c="var(--ink-3)"
            style={{ whiteSpace: 'nowrap', fontVariantNumeric: 'tabular-nums' }}
          >
            {pageLabel} / {manifest.pageCount}
          </Text>

          <Tooltip label={forwardChapterLabel} withArrow zIndex={OVERLAY_Z}>
            <ActionIcon
              variant="subtle"
              color="gray"
              onClick={rtl ? onPrevChapter : onNextChapter}
              disabled={(rtl ? manifest.previousChapterId : manifest.nextChapterId) === null}
              aria-label={forwardChapterLabel}
            >
              <IconChevronRight size={20} />
            </ActionIcon>
          </Tooltip>

          <Tooltip label={t`Page thumbnails`} withArrow zIndex={OVERLAY_Z}>
            <ActionIcon
              variant={stripOpen ? 'light' : 'subtle'}
              color="gray"
              onClick={onToggleStrip}
              aria-label={t`Toggle page thumbnails`}
            >
              <IconLayoutGrid size={20} />
            </ActionIcon>
          </Tooltip>

          <ReaderSettings
            opened={settingsOpen}
            onOpenedChange={setSettingsOpen}
            manifest={manifest}
            prefs={prefs}
            onPrefs={onPrefs}
            autoLayout={autoLayout}
            selection={selection}
            onSelection={onSelection}
            source={source}
            autoProfileId={autoProfileId}
            profiles={profiles}
            incognito={incognito}
            onIncognito={onIncognito}
            previewSrc={thumbs[page]}
          />

          {/* No keyboard on a touch screen, so nothing for this to list there. */}
          <Tooltip label={t`Keyboard shortcuts (?)`} withArrow zIndex={OVERLAY_Z}>
            <ActionIcon
              variant="subtle"
              color="gray"
              className="reader-shortcuts-button"
              onClick={onShortcuts}
              aria-label={t`Keyboard shortcuts`}
            >
              <IconKeyboard size={20} />
            </ActionIcon>
          </Tooltip>

          {prefs.mode === 'vertical' && autoScroll && (
            <Group gap={2} wrap="nowrap">
              {autoScroll.on && (
                <Tooltip label={t`Slower ([)`} withArrow zIndex={OVERLAY_Z}>
                  <ActionIcon
                    variant="subtle"
                    color="gray"
                    onClick={() => autoScroll.onLevel(autoScroll.level - 1)}
                    disabled={autoScroll.level <= AUTO_SCROLL_MIN}
                    aria-label={t`Scroll slower`}
                  >
                    <IconMinus size={18} />
                  </ActionIcon>
                </Tooltip>
              )}
              <Tooltip label={autoScroll.on ? t`Stop auto-scroll (A)` : t`Auto-scroll (A)`} withArrow zIndex={OVERLAY_Z}>
                <ActionIcon
                  variant={autoScroll.on ? 'light' : 'subtle'}
                  color="gray"
                  onClick={autoScroll.onToggle}
                  aria-label={autoScroll.on ? t`Stop auto-scroll` : t`Auto-scroll`}
                  aria-pressed={autoScroll.on}
                >
                  {autoScroll.on ? <IconPlayerPause size={20} /> : <IconPlayerPlay size={20} />}
                </ActionIcon>
              </Tooltip>
              {autoScroll.on && (
                <>
                  <Text
                    fz="xs"
                    c="var(--ink-3)"
                    w="2ch"
                    ta="center"
                    style={{ fontVariantNumeric: 'tabular-nums' }}
                    aria-label={t`Speed ${autoScroll.level} of ${AUTO_SCROLL_MAX}`}
                  >
                    {autoScroll.level}
                  </Text>
                  <Tooltip label={t`Faster (])`} withArrow zIndex={OVERLAY_Z}>
                    <ActionIcon
                      variant="subtle"
                      color="gray"
                      onClick={() => autoScroll.onLevel(autoScroll.level + 1)}
                      disabled={autoScroll.level >= AUTO_SCROLL_MAX}
                      aria-label={t`Scroll faster`}
                    >
                      <IconPlus size={18} />
                    </ActionIcon>
                  </Tooltip>
                </>
              )}
            </Group>
          )}

          {prefs.mode !== 'vertical' && (
            <>
              {zoom !== 1 && (
                <Tooltip label={t`Reset zoom`} withArrow zIndex={OVERLAY_Z}>
                  <ActionIcon variant="light" color="gray" onClick={onZoomReset} aria-label={t`Reset zoom`}>
                    <IconZoomReset size={20} />
                  </ActionIcon>
                </Tooltip>
              )}
              <Tooltip label={t`Zoom out`} withArrow zIndex={OVERLAY_Z}>
                <ActionIcon
                  variant="subtle"
                  color="gray"
                  onClick={onZoomOut}
                  disabled={zoom <= 1}
                  aria-label={t`Zoom out`}
                >
                  <IconZoomOut size={20} />
                </ActionIcon>
              </Tooltip>
              <Tooltip label={t`Zoom in`} withArrow zIndex={OVERLAY_Z}>
                <ActionIcon
                  variant="subtle"
                  color="gray"
                  onClick={onZoomIn}
                  disabled={zoom >= zoomMax}
                  aria-label={t`Zoom in`}
                >
                  <IconZoomIn size={20} />
                </ActionIcon>
              </Tooltip>
            </>
          )}

          {document.fullscreenEnabled && (
            <Tooltip label={fullscreen ? t`Exit full screen` : t`Full screen`} withArrow zIndex={OVERLAY_Z}>
              <ActionIcon
                variant="subtle"
                color="gray"
                onClick={onToggleFullscreen}
                aria-label={t`Toggle full screen`}
              >
                {fullscreen ? <IconMinimize size={20} /> : <IconMaximize size={20} />}
              </ActionIcon>
            </Tooltip>
          )}
        </Group>
      </div>
    </>
  )
}
