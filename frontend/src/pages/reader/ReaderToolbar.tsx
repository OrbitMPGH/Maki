import {
  ActionIcon,
  Group,
  MultiSelect,
  Popover,
  Progress,
  SegmentedControl,
  Select,
  Slider,
  Stack,
  Switch,
  Text,
  Tooltip,
} from '@mantine/core'
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
  IconSettings,
  IconSquareHalf,
  IconZoomIn,
  IconZoomOut,
  IconZoomReset,
} from '@tabler/icons-react'
import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { Trans, Plural, useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'
import type { PrefsSource, ReaderManifest } from '../../api/reader'
import type { ReadingProfile } from '../../api/readingProfiles'
import { wakeLockSupported } from '../../lib/wakeLock'
import { useLabel } from '../../i18n-context'
import {
  BROWSER_ENHANCE_METHODS,
  ENHANCE_FALLBACKS,
  ENHANCE_LEVELS,
  ENHANCE_SKIP_TIERS,
  SERVER_ENHANCE_METHODS,
  enhanceMethodInfo,
  isServerMethod,
  serverModelId,
} from './enhanceMethods'
import { BACKGROUNDS, enhanceFor, type PrefsSelection, type ReaderPrefs } from './prefs'
import { setCompareHeld, useCompareHeld } from './compareHold'
import { useEnhanceEngine } from './enhanceRuntime'
import PageScrubber from './PageScrubber'

/**
 * The reader renders above Mantine's popover layer, so anything that portals to <body> has to
 * be lifted past it explicitly, otherwise it paints underneath the page and looks like the
 * control simply doesn't work.
 */
const OVERLAY_Z = 500

// Inside the settings popover, a portalled list counts as outside it: the first touch on the list
// closed both, so it could never be scrolled. Rendered in place, fixed so the popover does not clip it.
const NESTED_COMBOBOX = { zIndex: OVERLAY_Z + 1, withinPortal: false, floatingStrategy: 'fixed' } as const

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
  stripOpen,
  onToggleStrip,
  visible,
  onHold,
  onReveal,
  onShortcuts,
  serverEnhancing = false,
  enhancedPages,
  thumbs = [],
  shownTo,
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
  /** Thumbnail URL per page, shown above the slider under the pointer. */
  thumbs?: string[]
  /** The last page on screen, past `page` when a spread shows two. */
  shownTo?: number
}) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const { scale } = prefs

  // The slider and the chapter chevrons mirror in RTL: the left chevron goes to the next chapter,
  // so each chevron's disabled state follows the chapter it actually opens.
  const rtl = prefs.direction === 'rtl'
  const [settingsOpen, setSettingsOpen] = useState(false)

  // "Auto" names the profile the series' type resolves to, so choosing it says what it will do.
  const autoProfile = profiles.find((p) => p.id === autoProfileId)
  const autoProfileName = autoProfile?.name
  const autoLabel = autoProfile ? t`Auto (${autoProfileName})` : t`Auto (my defaults)`

  const inForce = profiles.find((p) => p.id === selection) ?? (selection === 'auto' ? autoProfile : undefined)
  const inForceName = inForce?.name
  const editsAffect =
    source === 'Series'
      ? t`Changes apply to this series only.`
      : source === 'Profile' && inForce
        ? t`Changes retune "${inForceName}", so every series using it.`
        : t`Changes apply to your reader defaults.`

  const enhanceOn = prefs.enhance !== 'off'
  const engineStatus = useEnhanceEngine(enhanceOn).status
  const browserUnsupported = engineStatus === 'unsupported'
  const compareHeld = useCompareHeld()
  const enhanceModels = manifest.enhanceServer?.models
  const enhanceOptions = [
    ...BROWSER_ENHANCE_METHODS.map((m) => ({ value: m.id, label: renderLabel(m.label), disabled: browserUnsupported })),
    ...SERVER_ENHANCE_METHODS.flatMap((m) => {
      const model = enhanceModels?.find((entry) => entry.id === serverModelId(m.id))
      if (!model && m.id !== prefs.enhanceMethod) return []
      return [{ value: m.id, label: renderLabel(m.label), disabled: !model || !model.ready }]
    }),
  ]
  const enhanceNote = (value: string): string | undefined => {
    const model = enhanceModels?.find((entry) => entry.id === serverModelId(value))
    if (serverModelId(value) !== null) {
      if (!model) return t`Not available`
      if (!model.ready) return t`Downloading`
    } else if (browserUnsupported) {
      return t`Not supported on this device`
    }
    const info = enhanceMethodInfo(value)
    return info ? renderLabel(info.hint) : undefined
  }

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

  // Escape closes the settings first. Mantine only closes on Escape when focus is inside the
  // dropdown, and focus is usually still on the gear, so the reader's own Escape (leave the reader)
  // would win. Captured on the window so it runs before the reader's listener, which skips a key
  // that is already handled.
  useEffect(() => {
    if (!settingsOpen) return
    const onKey = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return
      event.preventDefault()
      setSettingsOpen(false)
    }
    window.addEventListener('keydown', onKey, true)
    return () => window.removeEventListener('keydown', onKey, true)
  }, [settingsOpen])

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
            <IconArrowLeft size={18} />
          </ActionIcon>
          <div style={{ minWidth: 0, flex: 1 }}>
            <Text fz="sm" fw={600} truncate>
              {manifest.seriesTitle}
            </Text>
            <Group gap={8} wrap="nowrap" align="center">
              <Text fz="xs" c="var(--ink-3)" style={{ whiteSpace: 'nowrap' }}>
                {manifest.label}
              </Text>
              {manifest.seriesChapterCount > 0 && (
                <Tooltip label={seriesProgressTooltip} withArrow zIndex={OVERLAY_Z}>
                  {/* The series meter, not the page one: the bottom bar's slider is this chapter. */}
                  <Group gap={6} wrap="nowrap" align="center" style={{ minWidth: 0 }}>
                    <Progress
                      value={(chaptersRead / manifest.seriesChapterCount) * 100}
                      color="var(--info)"
                      radius="xl"
                      size="xs"
                      w={64}
                      style={{ flexShrink: 0 }}
                      aria-label={t`Chapters read in this series`}
                    />
                    <Text fz="xs" c="var(--ink-3)" truncate className="tnum">
                      {/* Named, since the bottom bar's bare page count sits right under it. */}
                      {chaptersRead < seriesChapterCount ? (
                        plural(seriesChapterCount, {
                          one: `${chaptersRead}/# chapter read`,
                          other: `${chaptersRead}/# chapters read`,
                        })
                      ) : (
                        <Trans>all read</Trans>
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
            <Tooltip label={t`Hold to compare with the original (H)`} withArrow zIndex={OVERLAY_Z}>
              <ActionIcon
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
                <IconSquareHalf size={18} />
              </ActionIcon>
            </Tooltip>
          )}
          {incognito && (
            <Tooltip label={t`Incognito, this session isn't being recorded`} withArrow zIndex={OVERLAY_Z}>
              <IconEyeOff size={18} opacity={0.7} />
            </Tooltip>
          )}
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
              color={bookmarked ? 'var(--warn)' : 'gray'}
              onClick={onToggleBookmark}
              disabled={bookmarkDisabled}
              aria-label={t`Toggle bookmark`}
            >
              {bookmarked ? <IconBookmarkFilled size={18} /> : <IconBookmark size={18} />}
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
        <Group gap="xs" wrap="nowrap" px="md" h="100%">
          <Tooltip label={backChapterLabel} withArrow zIndex={OVERLAY_Z}>
            <ActionIcon
              variant="subtle"
              color="gray"
              onClick={rtl ? onNextChapter : onPrevChapter}
              disabled={(rtl ? manifest.nextChapterId : manifest.previousChapterId) === null}
              aria-label={backChapterLabel}
            >
              <IconChevronLeft size={18} />
            </ActionIcon>
          </Tooltip>

          <PageScrubber
            count={manifest.pageCount}
            page={page}
            shownTo={shownTo ?? page}
            rtl={rtl}
            onSeek={onSeek}
            enhanced={enhancedPages}
            thumbs={thumbs}
          />

          {/* Dropped on a phone: seven controls plus this counter leave the slider about 100px of
              track, and the counter is the one thing here that is also on the slider's own label
              and in the page badge. */}
          <Text
            visibleFrom="xs"
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
              <IconChevronRight size={18} />
            </ActionIcon>
          </Tooltip>

          <Tooltip label={t`Page thumbnails`} withArrow zIndex={OVERLAY_Z}>
            <ActionIcon
              variant={stripOpen ? 'light' : 'subtle'}
              color="gray"
              onClick={onToggleStrip}
              aria-label={t`Toggle page thumbnails`}
            >
              <IconLayoutGrid size={18} />
            </ActionIcon>
          </Tooltip>

          <Popover
            width={280}
            position="top-end"
            withArrow
            shadow="md"
            zIndex={OVERLAY_Z}
            opened={settingsOpen}
            onChange={setSettingsOpen}
          >
            <Popover.Target>
              <ActionIcon
                variant={settingsOpen ? 'light' : 'subtle'}
                color="gray"
                onClick={() => setSettingsOpen((open) => !open)}
                aria-label={t`Reader settings`}
              >
                <IconSettings size={18} />
              </ActionIcon>
            </Popover.Target>
            {/* Taller than a landscape phone or a short window: it scrolls rather than running off the top. */}
            <Popover.Dropdown style={{ maxHeight: 'calc(100dvh - 72px)', overflowY: 'auto' }}>
              <Stack gap="sm">
                <div>
                  <Text fz="xs" c="var(--ink-3)" mb={4}>
                    <Trans>Layout</Trans>
                  </Text>
                  <SegmentedControl
                    aria-label={t`Layout`}
                    fullWidth
                    size="xs"
                    value={prefs.mode}
                    onChange={(value) => onPrefs({ mode: value as ReaderPrefs['mode'] })}
                    data={[
                      { label: t`Single`, value: 'paged' },
                      { label: t`Double`, value: 'double' },
                      { label: t`Continuous`, value: 'vertical' },
                    ]}
                  />
                  {autoLayout && (
                    <Text fz="xs" c="var(--ink-3)" mt={4}>
                      <Trans>Continuous, because this chapter's pages are very tall.</Trans>
                    </Text>
                  )}
                </div>
                {prefs.mode === 'paged' && (
                  <div>
                    <Text fz="xs" c="var(--ink-3)" mb={4}>
                      <Trans>Wide pages</Trans>
                    </Text>
                    <SegmentedControl
                      aria-label={t`Wide pages`}
                      fullWidth
                      size="xs"
                      value={prefs.splitWide}
                      onChange={(value) => onPrefs({ splitWide: value as ReaderPrefs['splitWide'] })}
                      data={[
                        { label: t`Whole`, value: 'off' },
                        { label: t`Split`, value: 'split' },
                        { label: t`Rotate`, value: 'rotate' },
                      ]}
                    />
                  </div>
                )}
                <div>
                  <Text fz="xs" c="var(--ink-3)" mb={4}>
                    <Trans>Direction</Trans>
                  </Text>
                  <SegmentedControl
                    aria-label={t`Direction`}
                    fullWidth
                    size="xs"
                    value={prefs.direction}
                    onChange={(value) => onPrefs({ direction: value as ReaderPrefs['direction'] })}
                    data={[
                      { label: t`Left to right`, value: 'ltr' },
                      { label: t`Right to left`, value: 'rtl' },
                    ]}
                  />
                </div>
                <div>
                  <Text fz="xs" c="var(--ink-3)" mb={4}>
                    <Trans>Fit</Trans>
                  </Text>
                  <SegmentedControl
                    aria-label={t`Fit`}
                    fullWidth
                    size="xs"
                    value={prefs.fit}
                    onChange={(value) => onPrefs({ fit: value as ReaderPrefs['fit'] })}
                    data={[
                      { label: t`Width`, value: 'width' },
                      { label: t`Height`, value: 'height' },
                      { label: t`Screen`, value: 'screen' },
                      { label: '1:1', value: 'original' },
                    ]}
                  />
                </div>
                {prefs.fit === 'original' && (
                  <div>
                    <Text fz="xs" c="var(--ink-3)" mb={4}>
                      <Trans>Scale ({scale}%)</Trans>
                    </Text>
                    <Slider
                      thumbLabel={t`Scale`}
                      size="xs"
                      min={25}
                      max={400}
                      step={5}
                      value={prefs.scale}
                      onChange={(value) => onPrefs({ scale: value })}
                    />
                  </div>
                )}
                <div>
                  <Text fz="xs" c="var(--ink-3)" mb={4}>
                    <Trans>Background</Trans>
                  </Text>
                  <SegmentedControl
                    aria-label={t`Background`}
                    fullWidth
                    size="xs"
                    value={prefs.background === BACKGROUNDS.oled ? 'oled' : 'dark'}
                    onChange={(value) =>
                      onPrefs({ background: value === 'oled' ? BACKGROUNDS.oled : BACKGROUNDS.dark })
                    }
                    data={[
                      { label: t`Dark`, value: 'dark' },
                      { label: t`OLED black`, value: 'oled' },
                    ]}
                  />
                </div>
                <div>
                  <Text fz="xs" c="var(--ink-3)" mb={4}>
                    <Trans>Dim ({prefs.dim}%)</Trans>
                  </Text>
                  <Slider
                    thumbLabel={t`Dim`}
                    size="xs"
                    min={0}
                    max={80}
                    step={5}
                    label={null}
                    value={prefs.dim}
                    onChange={(value) => onPrefs({ dim: value })}
                  />
                </div>
                <div>
                  <Text fz="xs" c="var(--ink-3)" mb={4}>
                    <Trans>Colour filter</Trans>
                  </Text>
                  <SegmentedControl
                    aria-label={t`Colour filter`}
                    fullWidth
                    size="xs"
                    value={prefs.filter}
                    onChange={(value) => onPrefs({ filter: value as ReaderPrefs['filter'] })}
                    data={[
                      { label: t`None`, value: 'none' },
                      { label: t`Gray`, value: 'grayscale' },
                      { label: t`Sepia`, value: 'sepia' },
                      { label: t`Invert`, value: 'invert' },
                    ]}
                  />
                </div>
                <div>
                  <Text fz="xs" c="var(--ink-3)" mb={4}>
                    <Trans>Enhance</Trans>
                  </Text>
                  <SegmentedControl
                    aria-label={t`Enhance`}
                    fullWidth
                    size="xs"
                    value={prefs.enhance}
                    onChange={(value) => onPrefs({ enhance: value as ReaderPrefs['enhance'] })}
                    data={ENHANCE_LEVELS.map((level) => ({ label: renderLabel(level.label), value: level.value }))}
                  />
                  {prefs.enhance !== 'off' && enhanceFor(prefs, manifest.tier) === 'off' && (
                    <Text fz="xs" c="var(--ink-3)" mt={4}>
                      <Trans>Off for this chapter because of its quality.</Trans>
                    </Text>
                  )}
                </div>
                {prefs.enhance !== 'off' && (
                  <div>
                    <Text fz="xs" c="var(--ink-3)" mb={4}>
                      <Trans>Method</Trans>
                    </Text>
                    <Select
                      aria-label={t`Method`}
                      size="xs"
                      comboboxProps={NESTED_COMBOBOX}
                      allowDeselect={false}
                      value={prefs.enhanceMethod}
                      onChange={(value) => value && onPrefs({ enhanceMethod: value as ReaderPrefs['enhanceMethod'] })}
                      data={enhanceOptions}
                      renderOption={({ option }) => (
                        <div>
                          <Text fz="xs">{option.label}</Text>
                          <Text fz="xs" c="var(--ink-3)">
                            {enhanceNote(option.value)}
                          </Text>
                        </div>
                      )}
                    />
                  </div>
                )}
                {prefs.enhance !== 'off' && (
                  <div>
                    <Text fz="xs" c="var(--ink-3)" mb={4}>
                      <Trans>Don't enhance</Trans>
                    </Text>
                    <MultiSelect
                      aria-label={t`Don't enhance`}
                      size="xs"
                      comboboxProps={NESTED_COMBOBOX}
                      placeholder={t`Every quality is enhanced`}
                      value={prefs.enhanceSkipTiers}
                      onChange={(value) => onPrefs({ enhanceSkipTiers: value as ReaderPrefs['enhanceSkipTiers'] })}
                      data={ENHANCE_SKIP_TIERS.map((tier) => ({ value: tier.value, label: renderLabel(tier.label) }))}
                    />
                  </div>
                )}
                {prefs.enhance !== 'off' && isServerMethod(prefs.enhanceMethod) && (
                  <div>
                    <Text fz="xs" c="var(--ink-3)" mb={4}>
                      <Trans>While the server works</Trans>
                    </Text>
                    <SegmentedControl
                      aria-label={t`While the server works`}
                      fullWidth
                      size="xs"
                      value={prefs.enhanceFallback}
                      onChange={(value) => onPrefs({ enhanceFallback: value as ReaderPrefs['enhanceFallback'] })}
                      data={ENHANCE_FALLBACKS.map((f) => ({
                        label: renderLabel(f.label),
                        value: f.value,
                        disabled: browserUnsupported && f.value !== 'off',
                      }))}
                    />
                  </div>
                )}
                <Switch
                  size="xs"
                  label={t`Crop borders`}
                  checked={prefs.cropBorders}
                  onChange={(event) => onPrefs({ cropBorders: event.currentTarget.checked })}
                />
                <Switch
                  size="xs"
                  label={t`Scroll webtoon chapters`}
                  checked={prefs.autoWebtoon}
                  onChange={(event) => onPrefs({ autoWebtoon: event.currentTarget.checked })}
                />
                <Switch
                  size="xs"
                  label={t`Tap zones`}
                  checked={prefs.tapZones}
                  onChange={(event) => onPrefs({ tapZones: event.currentTarget.checked })}
                />
                <Switch
                  size="xs"
                  label={t`Show page number`}
                  checked={prefs.showPageNumber}
                  onChange={(event) => onPrefs({ showPageNumber: event.currentTarget.checked })}
                />
                <Switch
                  size="xs"
                  label={t`Flash chapter name on chapter change`}
                  checked={prefs.chapterBanner}
                  onChange={(event) => onPrefs({ chapterBanner: event.currentTarget.checked })}
                />
                <Switch
                  size="xs"
                  label={t`Auto-advance to next chapter`}
                  checked={prefs.autoNextChapter}
                  onChange={(event) => onPrefs({ autoNextChapter: event.currentTarget.checked })}
                />
                {wakeLockSupported() && (
                  <Switch
                    size="xs"
                    label={t`Keep the screen awake`}
                    checked={prefs.keepAwake}
                    onChange={(event) => onPrefs({ keepAwake: event.currentTarget.checked })}
                  />
                )}
                <Switch
                  size="xs"
                  label={t`Incognito (don't record this session)`}
                  checked={incognito}
                  onChange={(event) => onIncognito(event.currentTarget.checked)}
                />

                <div>
                  <Text fz="xs" c="var(--ink-3)" mb={4}>
                    <Trans>Reading profile</Trans>
                  </Text>
                  <Select
                    aria-label={t`Reading profile`}
                    size="xs"
                    comboboxProps={NESTED_COMBOBOX}
                    allowDeselect={false}
                    value={String(selection)}
                    onChange={(value) => {
                      if (!value) return
                      onSelection(value === 'auto' || value === 'series' ? value : Number(value))
                    }}
                    data={[
                      { label: autoLabel, value: 'auto' },
                      ...profiles.map((p) => ({ label: p.name, value: String(p.id) })),
                      { label: t`Just this series`, value: 'series' },
                    ]}
                  />
                  <Text fz="xs" c="var(--ink-3)" mt={4}>
                    {editsAffect}
                  </Text>
                </div>
              </Stack>
            </Popover.Dropdown>
          </Popover>

          {/* No keyboard on a touch screen, so nothing for this to list there. */}
          <Tooltip label={t`Keyboard shortcuts (?)`} withArrow zIndex={OVERLAY_Z}>
            <ActionIcon
              variant="subtle"
              color="gray"
              className="reader-shortcuts-button"
              onClick={onShortcuts}
              aria-label={t`Keyboard shortcuts`}
            >
              <IconKeyboard size={18} />
            </ActionIcon>
          </Tooltip>

          {prefs.mode !== 'vertical' && (
            <>
              {zoom !== 1 && (
                <Tooltip label={t`Reset zoom`} withArrow zIndex={OVERLAY_Z}>
                  <ActionIcon variant="light" color="gray" onClick={onZoomReset} aria-label={t`Reset zoom`}>
                    <IconZoomReset size={18} />
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
                  <IconZoomOut size={18} />
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
                  <IconZoomIn size={18} />
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
                {fullscreen ? <IconMinimize size={18} /> : <IconMaximize size={18} />}
              </ActionIcon>
            </Tooltip>
          )}
        </Group>
      </div>
    </>
  )
}
