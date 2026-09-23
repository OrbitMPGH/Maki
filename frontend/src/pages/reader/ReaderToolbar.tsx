import {
  ActionIcon,
  Group,
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
  IconLayoutGrid,
  IconMaximize,
  IconMinimize,
  IconSettings,
} from '@tabler/icons-react'
import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { Trans, Plural, useLingui } from '@lingui/react/macro'
import type { PrefsSource, ReaderManifest } from '../../api/reader'
import type { ReadingProfile } from '../../api/readingProfiles'
import { BACKGROUNDS, type PrefsSelection, type ReaderPrefs } from './prefs'

/**
 * The reader renders above Mantine's popover layer, so anything that portals to <body> has to
 * be lifted past it explicitly, otherwise it paints underneath the page and looks like the
 * control simply doesn't work.
 */
const OVERLAY_Z = 500

export default function ReaderToolbar({
  manifest,
  page,
  onSeek,
  onPrevChapter,
  onNextChapter,
  prefs,
  onPrefs,
  selection,
  onSelection,
  source,
  autoProfileId,
  profiles,
  fullscreen,
  onToggleFullscreen,
  incognito,
  onIncognito,
  bookmarked,
  onToggleBookmark,
  stripOpen,
  onToggleStrip,
  visible,
  onHold,
}: {
  manifest: ReaderManifest
  page: number
  onSeek: (page: number) => void
  onPrevChapter: () => void
  onNextChapter: () => void
  prefs: ReaderPrefs
  onPrefs: (patch: Partial<ReaderPrefs>) => void
  selection: PrefsSelection
  onSelection: (selection: PrefsSelection) => void
  source: PrefsSource
  autoProfileId: number | null
  profiles: ReadingProfile[]
  fullscreen: boolean
  onToggleFullscreen: () => void
  incognito: boolean
  onIncognito: (value: boolean) => void
  bookmarked: boolean
  onToggleBookmark: () => void
  stripOpen: boolean
  onToggleStrip: () => void
  visible: boolean
  /** Keeps the auto-hide from pulling the chrome out from under an open menu or the cursor. */
  onHold: (held: boolean) => void
}) {
  const { t } = useLingui()
  const { scale } = prefs

  // Only the slider mirrors: it is a spatial map of the pages. The chapter chevrons stay
  // previous-left / next-right in both directions: they're semantic controls, not positions.
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

  // Shared between the tooltip and the icon button's own aria-label, which say the same thing.
  const backChapterLabel = rtl ? t`Next chapter` : t`Previous chapter`
  const forwardChapterLabel = rtl ? t`Previous chapter` : t`Next chapter`

  useEffect(() => {
    onHold(settingsOpen)
  }, [settingsOpen, onHold])

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
  // the denominator, completed ones as the numerator. The manifest's counts are a snapshot from
  // when the chapter opened, so finishing this one on screen is added here rather than waited for —
  // the condition mirrors the server's ("the last page means read"), so the optimistic number is
  // the one the next manifest fetch comes back with. Incognito writes nothing, so it adds nothing.
  const readingCounted = !incognito && !manifest.completed && page >= manifest.pageCount - 1
  const chaptersRead = Math.min(
    manifest.seriesChapterCount,
    manifest.seriesReadCount + (readingCounted ? 1 : 0),
  )
  const chaptersLeft = Math.max(0, manifest.seriesChapterCount - chaptersRead)
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
        onMouseEnter={() => onHold(true)}
        onMouseLeave={() => onHold(settingsOpen)}
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
              <Text fz="xs" c="dimmed" style={{ whiteSpace: 'nowrap' }}>
                {manifest.label}
              </Text>
              {manifest.seriesChapterCount > 0 && (
                <Tooltip label={seriesProgressTooltip} withArrow zIndex={OVERLAY_Z}>
                  {/* The series meter, not the page one: the bottom bar's slider is this chapter. */}
                  <Group gap={6} wrap="nowrap" align="center" style={{ flexShrink: 0 }}>
                    <Progress
                      value={(chaptersRead / manifest.seriesChapterCount) * 100}
                      color="var(--info)"
                      radius="xl"
                      size="xs"
                      w={64}
                      aria-label={t`Chapters read in this series`}
                    />
                    <Text fz="xs" c="dimmed" style={{ whiteSpace: 'nowrap' }} className="tnum">
                      {chaptersRead}/{seriesChapterCount}
                      {' · '}
                      {chaptersLeft > 0 ? (
                        <Plural value={chaptersLeft} one="# left" other="# left" />
                      ) : (
                        <Trans>all read</Trans>
                      )}
                      {moreInSeries && (
                        <>
                          {' · '}
                          <Plural value={seriesWantedCount} one="# in series" other="# in series" />
                        </>
                      )}
                    </Text>
                  </Group>
                </Tooltip>
              )}
            </Group>
          </div>
          {incognito && (
            <Tooltip label={t`Incognito, this session isn't being recorded`} withArrow zIndex={OVERLAY_Z}>
              <IconEyeOff size={18} opacity={0.7} />
            </Tooltip>
          )}
          <Tooltip
            label={bookmarked ? t`Remove bookmark` : t`Bookmark this page`}
            withArrow
            zIndex={OVERLAY_Z}
          >
            <ActionIcon
              variant="subtle"
              color={bookmarked ? 'yellow' : 'gray'}
              onClick={onToggleBookmark}
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
        onMouseEnter={() => onHold(true)}
        onMouseLeave={() => onHold(settingsOpen)}
      >
        <Group gap="xs" wrap="nowrap" px="md" h="100%">
          <Tooltip label={backChapterLabel} withArrow zIndex={OVERLAY_Z}>
            <ActionIcon
              variant="subtle"
              color="gray"
              onClick={rtl ? onNextChapter : onPrevChapter}
              disabled={manifest.previousChapterId === null}
              aria-label={backChapterLabel}
            >
              <IconChevronLeft size={18} />
            </ActionIcon>
          </Tooltip>

          {/*
            Right-to-left reading puts page 1 at the right end of the track. That has to be done
            by inverting the value, not with CSS `direction`: Mantine positions the thumb with an
            inline `left: %`, so a flipped container moves the fill but leaves the thumb sliding
            the wrong way. `inverted` then moves the filled portion to the right of the thumb, so
            it still grows from where you started reading rather than from the far end.
          */}
          <Slider
            className="reader-slider"
            min={1}
            max={Math.max(1, manifest.pageCount)}
            value={rtl ? manifest.pageCount - page : page + 1}
            onChange={(value) => onSeek(rtl ? manifest.pageCount - value : value - 1)}
            label={(value) => `${rtl ? manifest.pageCount - value + 1 : value} / ${manifest.pageCount}`}
            inverted={rtl}
            style={{ flex: 1 }}
          />

          {/* Dropped on a phone: seven controls plus this counter leave the slider about 100px of
              track, and the counter is the one thing here that is also on the slider's own label
              and in the page badge. */}
          <Text
            visibleFrom="xs"
            fz="xs"
            c="dimmed"
            style={{ whiteSpace: 'nowrap', fontVariantNumeric: 'tabular-nums' }}
          >
            {page + 1} / {manifest.pageCount}
          </Text>

          <Tooltip label={forwardChapterLabel} withArrow zIndex={OVERLAY_Z}>
            <ActionIcon
              variant="subtle"
              color="gray"
              onClick={rtl ? onPrevChapter : onNextChapter}
              disabled={manifest.nextChapterId === null}
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
            <Popover.Dropdown>
              <Stack gap="sm">
                <div>
                  <Text fz="xs" c="dimmed" mb={4}>
                    <Trans>Layout</Trans>
                  </Text>
                  <SegmentedControl
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
                </div>
                <div>
                  <Text fz="xs" c="dimmed" mb={4}>
                    <Trans>Direction</Trans>
                  </Text>
                  <SegmentedControl
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
                  <Text fz="xs" c="dimmed" mb={4}>
                    <Trans>Fit</Trans>
                  </Text>
                  <SegmentedControl
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
                    <Text fz="xs" c="dimmed" mb={4}>
                      <Trans>Scale ({scale}%)</Trans>
                    </Text>
                    <Slider
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
                  <Text fz="xs" c="dimmed" mb={4}>
                    <Trans>Background</Trans>
                  </Text>
                  <SegmentedControl
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
                <Switch
                  size="xs"
                  label={t`Incognito (don't record this session)`}
                  checked={incognito}
                  onChange={(event) => onIncognito(event.currentTarget.checked)}
                />

                <div>
                  <Text fz="xs" c="dimmed" mb={4}>
                    <Trans>Reading profile</Trans>
                  </Text>
                  <Select
                    size="xs"
                    comboboxProps={{ zIndex: OVERLAY_Z + 1 }}
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
                  <Text fz="xs" c="dimmed" mt={4}>
                    {editsAffect}
                  </Text>
                </div>
              </Stack>
            </Popover.Dropdown>
          </Popover>

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
        </Group>
      </div>
    </>
  )
}
