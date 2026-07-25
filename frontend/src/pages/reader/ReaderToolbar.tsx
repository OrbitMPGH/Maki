import {
  ActionIcon,
  Group,
  Popover,
  SegmentedControl,
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
import { Link } from 'react-router-dom'
import type { ReaderManifest } from '../../api/reader'
import type { PrefsScope, ReaderPrefs } from './prefs'

export default function ReaderToolbar({
  manifest,
  page,
  onSeek,
  onPrevChapter,
  onNextChapter,
  prefs,
  onPrefs,
  scope,
  onScope,
  fullscreen,
  onToggleFullscreen,
  incognito,
  onIncognito,
  bookmarked,
  onToggleBookmark,
  stripOpen,
  onToggleStrip,
  visible,
}: {
  manifest: ReaderManifest
  page: number
  onSeek: (page: number) => void
  onPrevChapter: () => void
  onNextChapter: () => void
  prefs: ReaderPrefs
  onPrefs: (patch: Partial<ReaderPrefs>) => void
  scope: PrefsScope
  onScope: (scope: PrefsScope) => void
  fullscreen: boolean
  onToggleFullscreen: () => void
  incognito: boolean
  onIncognito: (value: boolean) => void
  bookmarked: boolean
  onToggleBookmark: () => void
  stripOpen: boolean
  onToggleStrip: () => void
  visible: boolean
}) {
  // Only the slider mirrors: it is a spatial map of the pages. The chapter chevrons stay
  // previous-left / next-right in both directions — they're semantic controls, not positions.
  const rtl = prefs.direction === 'rtl'

  return (
    <>
      <div className="reader-bar reader-bar-top" data-visible={visible}>
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
            <Text fz="xs" c="dimmed">
              {manifest.label}
            </Text>
          </div>
          {incognito && (
            <Tooltip label="Incognito — this session isn't being recorded" withArrow>
              <IconEyeOff size={18} opacity={0.7} />
            </Tooltip>
          )}
          <Tooltip label={bookmarked ? 'Remove bookmark' : 'Bookmark this page'} withArrow>
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

      <div className="reader-bar reader-bar-bottom" data-visible={visible}>
        <Group gap="xs" wrap="nowrap" px="md" h="100%">
          <Tooltip label="Previous chapter" withArrow>
            <ActionIcon
              variant="subtle"
              color="gray"
              onClick={onPrevChapter}
              disabled={manifest.previousChapterId === null}
              aria-label="Previous chapter"
            >
              <IconChevronLeft size={18} />
            </ActionIcon>
          </Tooltip>

          {/*
            Right-to-left reading puts page 1 at the right end of the track. That has to be done
            by inverting the value, not with CSS `direction`: Mantine positions the thumb with an
            inline `left: %`, so a flipped container moves the fill but leaves the thumb sliding
            the wrong way.
          */}
          <Slider
            className="reader-slider"
            min={1}
            max={Math.max(1, manifest.pageCount)}
            value={rtl ? manifest.pageCount - page : page + 1}
            onChange={(value) => onSeek(rtl ? manifest.pageCount - value : value - 1)}
            label={(value) => `${rtl ? manifest.pageCount - value + 1 : value} / ${manifest.pageCount}`}
            style={{ flex: 1 }}
          />

          <Text fz="xs" c="dimmed" style={{ whiteSpace: 'nowrap', fontVariantNumeric: 'tabular-nums' }}>
            {page + 1} / {manifest.pageCount}
          </Text>

          <Tooltip label="Next chapter" withArrow>
            <ActionIcon
              variant="subtle"
              color="gray"
              onClick={onNextChapter}
              disabled={manifest.nextChapterId === null}
              aria-label="Next chapter"
            >
              <IconChevronRight size={18} />
            </ActionIcon>
          </Tooltip>

          <Tooltip label="Page thumbnails" withArrow>
            <ActionIcon
              variant={stripOpen ? 'light' : 'subtle'}
              color="gray"
              onClick={onToggleStrip}
              aria-label="Toggle page thumbnails"
            >
              <IconLayoutGrid size={18} />
            </ActionIcon>
          </Tooltip>

          <Popover width={280} position="top-end" withArrow shadow="md">
            <Popover.Target>
              <ActionIcon variant="subtle" color="gray" aria-label="Reader settings">
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
                    These settings apply to
                  </Text>
                  <SegmentedControl
                    fullWidth
                    size="xs"
                    value={scope}
                    onChange={(value) => onScope(value as PrefsScope)}
                    data={[
                      { label: 'All series', value: 'global' },
                      { label: 'This series', value: 'series' },
                    ]}
                  />
                </div>
              </Stack>
            </Popover.Dropdown>
          </Popover>

          <Tooltip label={fullscreen ? 'Exit full screen' : 'Full screen'} withArrow>
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
