import { useMemo } from 'react'
import { Box, Group, Text, Title, Tooltip } from '@mantine/core'
import { Trans, Plural, useLingui } from '@lingui/react/macro'
import type { HeatmapDay } from '../../api/hooks'
import { Panel } from '../../components/ui/Panel'
import { formatDate, monthName } from '../../format'

const WEEKS = 53
const DAYS_IN_WEEK = 7

/**
 * Five buckets, thresholded on chapters. Fixed cut points rather than quantiles of the user's own
 * history: a relative scale makes a quiet week look identical to a busy one, which is the one thing
 * a contribution grid exists to distinguish.
 */
function level(chapters: number, seconds: number): number {
  if (chapters === 0 && seconds === 0) return 0
  if (chapters >= 20) return 4
  if (chapters >= 8) return 3
  if (chapters >= 3) return 2
  return 1
}

const SHADES = [
  'var(--surface-2, rgba(128,128,128,0.14))',
  'color-mix(in srgb, var(--brand) 25%, transparent)',
  'color-mix(in srgb, var(--brand) 45%, transparent)',
  'color-mix(in srgb, var(--brand) 70%, transparent)',
  'var(--brand)',
]

function isoDate(d: Date): string {
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}

/**
 * A GitHub-style year of reading. Columns are weeks, rows are weekdays starting Monday, with the
 * grid ending on the current week so today sits in the last column.
 */
export function ReadingHeatmap({ days }: { days: HeatmapDay[] }) {
  const { i18n } = useLingui()
  const { columns, monthLabels } = useMemo(() => {
    const byDate = new Map(days.map((d) => [d.date.slice(0, 10), d]))

    const today = new Date()
    // Walk back to the Monday of the current week, then back 52 more weeks.
    const end = new Date(today)
    end.setDate(end.getDate() - ((end.getDay() + 6) % 7))
    const start = new Date(end)
    start.setDate(start.getDate() - (WEEKS - 1) * DAYS_IN_WEEK)

    const cols: { date: string; chapters: number; seconds: number }[][] = []
    const labels: { index: number; label: string }[] = []
    let lastMonth = -1

    for (let w = 0; w < WEEKS; w++) {
      const column: { date: string; chapters: number; seconds: number }[] = []
      for (let d = 0; d < DAYS_IN_WEEK; d++) {
        const cell = new Date(start)
        cell.setDate(start.getDate() + w * DAYS_IN_WEEK + d)
        const key = isoDate(cell)
        const found = byDate.get(key)
        column.push({ date: key, chapters: found?.chapters ?? 0, seconds: found?.seconds ?? 0 })

        if (d === 0 && cell.getMonth() !== lastMonth) {
          lastMonth = cell.getMonth()
          labels.push({ index: w, label: monthName(cell.getMonth() + 1, 'short') })
        }
      }

      cols.push(column)
    }

    return { columns: cols, monthLabels: labels }
    // monthName is locale-bound: without i18n.locale here, a language switch would leave the
    // previous language's month names cached until days changed too.
  }, [days, i18n.locale])

  return (
    <Panel p="md">
      <Title order={4} mb="xs">
        <Trans>Reading days</Trans>
      </Title>
      <Box style={{ overflowX: 'auto' }}>
        <Box style={{ minWidth: WEEKS * 14 }}>
          <Box style={{ display: 'flex', gap: 3, marginBottom: 4, paddingLeft: 0 }}>
            {columns.map((_, i) => {
              const label = monthLabels.find((m) => m.index === i)
              return (
                <Box key={i} style={{ width: 11, fontSize: 9, color: 'var(--ink-3)' }}>
                  {label?.label ?? ''}
                </Box>
              )
            })}
          </Box>
          <Box style={{ display: 'flex', gap: 3 }}>
            {columns.map((column, i) => (
              <Box key={i} style={{ display: 'flex', flexDirection: 'column', gap: 3 }}>
                {column.map((cell) => {
                  // Appending a local time-of-day avoids the bare "yyyy-MM-dd" being read as UTC
                  // midnight, which would shift the printed date back a day west of UTC.
                  const cellDate = formatDate(`${cell.date}T00:00:00`)
                  const nothingRead = cell.chapters === 0 && cell.seconds === 0
                  return (
                    <Tooltip
                      key={cell.date}
                      label={
                        nothingRead ? (
                          <Trans>{cellDate}: nothing read</Trans>
                        ) : (
                          <Trans>
                            {cellDate}: <Plural value={cell.chapters} one="# chapter" other="# chapters" />
                          </Trans>
                        )
                      }
                      withArrow
                    >
                      <Box
                        style={{
                          width: 11,
                          height: 11,
                          borderRadius: 2,
                          background: SHADES[level(cell.chapters, cell.seconds)],
                        }}
                      />
                    </Tooltip>
                  )
                })}
              </Box>
            ))}
          </Box>
        </Box>
      </Box>
      <Group justify="flex-end" gap={4} mt="xs">
        <Text size="xs" c="var(--ink-3)">
          <Trans>Less</Trans>
        </Text>
        {SHADES.map((shade) => (
          <Box key={shade} style={{ width: 11, height: 11, borderRadius: 2, background: shade }} />
        ))}
        <Text size="xs" c="var(--ink-3)">
          <Trans>More</Trans>
        </Text>
      </Group>
    </Panel>
  )
}
