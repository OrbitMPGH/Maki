import { Badge, Checkbox, Group, Text, Tooltip } from '@mantine/core'
import { IconCircleCheckFilled, IconEye, IconEyeOff } from '@tabler/icons-react'
import { Link } from 'react-router-dom'
import type { SeriesDto } from '../../api/types'
import { seriesDownloadStateVisual, seriesStatusVisual } from './status'

/**
 * Poster card for the library grid — cover art is the hero, with a bottom
 * scrim carrying the title, a download-progress bar and status. Doubles as a
 * selection target in bulk mode.
 */
export function CoverCard({
  series,
  selectMode,
  selected,
  onToggle,
}: {
  series: SeriesDto
  selectMode: boolean
  selected: boolean
  onToggle: () => void
}) {
  const status = seriesStatusVisual(series.status)
  const download = seriesDownloadStateVisual(series)
  // Nothing monitored and nothing downloaded makes the normal total 0, which would render a
  // bare "0/?" next to a Chapters tab listing every known chapter as missing. Fall back to the
  // known count so the card reads "0/207", and mark it so it isn't mistaken for real progress.
  const monitoredTotal = series.chapterCount || 0
  const total = monitoredTotal || series.knownChapterCount || 0
  const unmonitored = monitoredTotal === 0 && total > 0
  const have = series.chapterFileCount
  const pct = !unmonitored && total > 0 ? Math.min(100, (have / total) * 100) : 0
  const complete = !unmonitored && total > 0 && have >= total

  return (
    <Link
      to={`/series/${series.id}`}
      className="cover-card"
      data-selected={selected || undefined}
      onClick={(e) => {
        if (selectMode) {
          e.preventDefault()
          onToggle()
        }
      }}
    >
      <div className="cover-poster">
        {series.coverUrl ? (
          <img src={series.coverUrl} alt={series.title} loading="lazy" />
        ) : (
          <div className="cover-placeholder">{series.title}</div>
        )}
        <div className="cover-scrim" />

        {selectMode && (
          <Checkbox
            checked={selected}
            readOnly
            size="sm"
            radius="sm"
            style={{ position: 'absolute', top: 8, left: 8, pointerEvents: 'none' }}
          />
        )}

        {/* In-flight download work, top-left. Absent when the series is idle. */}
        {download && (
          <Badge
            size="sm"
            variant="filled"
            color={download.color}
            leftSection={<download.Icon size={11} />}
            style={{ position: 'absolute', top: 8, left: 8, backdropFilter: 'blur(4px)' }}
          >
            {download.label}
          </Badge>
        )}

        <Group
          gap={6}
          style={{ position: 'absolute', top: 8, right: 8 }}
          wrap="nowrap"
        >
          {/* Monitor state on every card: a subtle eye when watched, a clear eye-off when not. */}
          <Tooltip label={series.monitored ? 'Monitored' : 'Not monitored'} withArrow>
            <Badge
              size="sm"
              circle
              variant="filled"
              color={series.monitored ? 'dark.7' : 'dark.9'}
              style={{ opacity: series.monitored ? 0.7 : 0.9 }}
            >
              {series.monitored ? <IconEye size={12} /> : <IconEyeOff size={12} />}
            </Badge>
          </Tooltip>
          <Badge
            size="sm"
            variant="filled"
            color={status.color}
            leftSection={<status.Icon size={11} />}
            style={{ backdropFilter: 'blur(4px)' }}
          >
            {status.label}
          </Badge>
        </Group>

        <div style={{ position: 'absolute', left: 10, right: 10, bottom: 9 }}>
          <Text fw={650} size="sm" c="white" lineClamp={2} lh={1.25} title={series.title}>
            {series.title}
          </Text>
          <Group justify="space-between" mt={7} gap={6} wrap="nowrap">
            <div
              style={{
                flex: 1,
                height: 4,
                borderRadius: 4,
                background: 'rgba(255,255,255,0.16)',
                overflow: 'hidden',
              }}
            >
              <div
                style={{
                  width: `${pct}%`,
                  height: '100%',
                  borderRadius: 4,
                  background: complete ? 'var(--ok)' : 'var(--brand)',
                }}
              />
            </div>
            <Group gap={3} wrap="nowrap">
              {complete && <IconCircleCheckFilled size={13} style={{ color: 'var(--ok)' }} />}
              <Tooltip
                label={`${total} chapter(s) known, none monitored — nothing will download`}
                withArrow
                disabled={!unmonitored}
              >
                <Text
                  size="xs"
                  c={unmonitored ? 'gray.6' : 'gray.4'}
                  className="tnum"
                  style={{ whiteSpace: 'nowrap' }}
                >
                  {have}/{total || '?'}
                </Text>
              </Tooltip>
            </Group>
          </Group>
        </div>
      </div>
    </Link>
  )
}
