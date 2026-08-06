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
  // Only the slider mirrors: it is a spatial map of the pages. The chapter chevrons stay
  // previous-left / next-right in both directions: they're semantic controls, not positions.
  const rtl = prefs.direction === 'rtl'
  const [settingsOpen, setSettingsOpen] = useState(false)

  // "Auto" names the profile the series' type resolves to, so choosing it says what it will do.
  const autoProfile = profiles.find((p) => p.id === autoProfileId)
  const autoLabel = autoProfile ? `Auto (${autoProfile.name})` : 'Auto (my defaults)'

  const inForce = profiles.find((p) => p.id === selection) ?? (selection === 'auto' ? autoProfile : undefined)
  const editsAffect =
    source === 'Series'
      ? 'Changes apply to this series only.'
      : source === 'Profile' && inForce
        ? `Changes retune "${inForce.name}", so every series using it.`
        : 'Changes apply to your reader defaults.'

  useEffect(() => {
    onHold(settingsOpen)
  }, [settingsOpen, onHold])

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
            aria-label="Back to series"
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
                <Tooltip
                  label={`${chaptersRead} of ${manifest.seriesChapterCount} downloaded chapters read`}
                  withArrow
                  zIndex={OVERLAY_Z}
                >
                  {/* The series meter, not the page one: the bottom bar's slider is this chapter. */}
                  <Group gap={6} wrap="nowrap" align="center" style={{ flexShrink: 0 }}>
                    <Progress
                      value={(chaptersRead / manifest.seriesChapterCount) * 100}
                      color="var(--info)"
                      radius="xl"
                      size="xs"
                      w={64}
                      aria-label="Chapters read in this series"
                    />
                    <Text fz="xs" c="dimmed" style={{ whiteSpace: 'nowrap' }} className="tnum">
                      {chaptersRead}/{manifest.seriesChapterCount}
                      {chaptersLeft > 0 ? ` · ${chaptersLeft} left` : ' · all read'}
                    </Text>
                  </Group>
                </Tooltip>
              )}
            </Group>
          </div>
          {incognito && (
            <Tooltip label="Incognito, this session isn't being recorded" withArrow zIndex={OVERLAY_Z}>
              <IconEyeOff size={18} opacity={0.7} />
            </Tooltip>
          )}
          <Tooltip
            label={bookmarked ? 'Remove bookmark' : 'Bookmark this page'}
            withArrow
            zIndex={OVERLAY_Z}
          >
            <ActionIcon
              variant="subtle"
              color={bookmarked ? 'yellow' : 'gray'}
              onClick={onToggleBookmark}
              aria-label="Toggle bookmark"
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
          <Tooltip label={rtl ? 'Next chapter' : 'Previous chapter'} withArrow zIndex={OVERLAY_Z}>
            <ActionIcon
              variant="subtle"
              color="gray"
              onClick={rtl ? onNextChapter : onPrevChapter}
              disabled={manifest.previousChapterId === null}
              aria-label={rtl ? 'Next chapter' : 'Previous chapter'}
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

          <Text fz="xs" c="dimmed" style={{ whiteSpace: 'nowrap', fontVariantNumeric: 'tabular-nums' }}>
            {page + 1} / {manifest.pageCount}
          </Text>

          <Tooltip label={rtl ? 'Previous chapter' : 'Next chapter'} withArrow zIndex={OVERLAY_Z}>
            <ActionIcon
              variant="subtle"
              color="gray"
              onClick={rtl ? onPrevChapter : onNextChapter}
              disabled={manifest.nextChapterId === null}
              aria-label={rtl ? 'Previous chapter' : 'Next chapter'}
            >
              <IconChevronRight size={18} />
            </ActionIcon>
          </Tooltip>

          <Tooltip label="Page thumbnails" withArrow zIndex={OVERLAY_Z}>
            <ActionIcon
              variant={stripOpen ? 'light' : 'subtle'}
              color="gray"
              onClick={onToggleStrip}
              aria-label="Toggle page thumbnails"
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
                aria-label="Reader settings"
              >
                <IconSettings size={18} />
              </ActionIcon>
            </Popover.Target>
            <Popover.Dropdown>
              <Stack gap="sm">
                <div>
                  <Text fz="xs" c="dimmed" mb={4}>
                    Layout
                  </Text>
                  <SegmentedControl
                    fullWidth
                    size="xs"
                    value={prefs.mode}
                    onChange={(value) => onPrefs({ mode: value as ReaderPrefs['mode'] })}
                    data={[
                      { label: 'Single', value: 'paged' },
                      { label: 'Double', value: 'double' },
                      { label: 'Continuous', value: 'vertical' },
                    ]}
                  />
                </div>
                <div>
                  <Text fz="xs" c="dimmed" mb={4}>
                    Direction
                  </Text>
                  <SegmentedControl
                    fullWidth
                    size="xs"
                    value={prefs.direction}
                    onChange={(value) => onPrefs({ direction: value as ReaderPrefs['direction'] })}
                    data={[
                      { label: 'Left to right', value: 'ltr' },
                      { label: 'Right to left', value: 'rtl' },
                    ]}
                  />
                </div>
                <div>
                  <Text fz="xs" c="dimmed" mb={4}>
                    Fit
                  </Text>
                  <SegmentedControl
                    fullWidth
                    size="xs"
                    value={prefs.fit}
                    onChange={(value) => onPrefs({ fit: value as ReaderPrefs['fit'] })}
                    data={[
                      { label: 'Width', value: 'width' },
                      { label: 'Height', value: 'height' },
                      { label: 'Screen', value: 'screen' },
                      { label: '1:1', value: 'original' },
                    ]}
                  />
                </div>
                <div>
                  <Text fz="xs" c="dimmed" mb={4}>
                    Background
                  </Text>
                  <SegmentedControl
                    fullWidth
                    size="xs"
                    value={prefs.background === BACKGROUNDS.oled ? 'oled' : 'dark'}
                    onChange={(value) =>
                      onPrefs({ background: value === 'oled' ? BACKGROUNDS.oled : BACKGROUNDS.dark })
                    }
                    data={[
                      { label: 'Dark', value: 'dark' },
                      { label: 'OLED black', value: 'oled' },
                    ]}
                  />
                </div>
                <Switch
                  size="xs"
                  label="Tap zones"
                  checked={prefs.tapZones}
                  onChange={(event) => onPrefs({ tapZones: event.currentTarget.checked })}
                />
                <Switch
                  size="xs"
                  label="Show page number"
                  checked={prefs.showPageNumber}
                  onChange={(event) => onPrefs({ showPageNumber: event.currentTarget.checked })}
                />
                <Switch
                  size="xs"
                  label="Auto-advance to next chapter"
                  checked={prefs.autoNextChapter}
                  onChange={(event) => onPrefs({ autoNextChapter: event.currentTarget.checked })}
                />
                <Switch
                  size="xs"
                  label="Incognito (don't record this session)"
                  checked={incognito}
                  onChange={(event) => onIncognito(event.currentTarget.checked)}
                />

                <div>
                  <Text fz="xs" c="dimmed" mb={4}>
                    Reading profile
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
                      { label: 'Just this series', value: 'series' },
                    ]}
                  />
                  <Text fz="xs" c="dimmed" mt={4}>
                    {editsAffect}
                  </Text>
                </div>
              </Stack>
            </Popover.Dropdown>
          </Popover>

          <Tooltip label={fullscreen ? 'Exit full screen' : 'Full screen'} withArrow zIndex={OVERLAY_Z}>
            <ActionIcon
              variant="subtle"
              color="gray"
              onClick={onToggleFullscreen}
              aria-label="Toggle full screen"
            >
              {fullscreen ? <IconMinimize size={18} /> : <IconMaximize size={18} />}
            </ActionIcon>
          </Tooltip>
        </Group>
      </div>
    </>
  )
}
