import { useMemo } from 'react'
import { Progress, Table, Text } from '@mantine/core'
import { useLingui } from '@lingui/react/macro'
import type { SourceReliabilityDto } from '../../api/stats'
import type { SourceUsage } from '../../api/hooks'
import { formatBytes, formatNumber, formatPercent, formatReadingTime } from '../../format'
import { PROTOCOL_LABELS, useNameLabel } from './labels'

interface SourceRow {
  name: string
  files: number
  bytes: number
  completed: number
  failed: number
  medianSecondsToComplete: number | null
}

// Source names come from two independent server tallies (what was downloaded, and what the queue
// did in the last 30 days), so a source can appear in one and not the other: a source with files on
// disk but no recent activity, or one the queue tried but that never finished a file.
function mergeRows(sources: SourceUsage[], reliability: SourceReliabilityDto[]): SourceRow[] {
  const rows = new Map<string, SourceRow>()
  for (const s of sources) {
    rows.set(s.name, { name: s.name, files: s.files, bytes: s.bytes, completed: 0, failed: 0, medianSecondsToComplete: null })
  }
  for (const r of reliability) {
    const existing = rows.get(r.name)
    if (existing) {
      existing.completed = r.completed
      existing.failed = r.failed
      existing.medianSecondsToComplete = r.medianSecondsToComplete
    } else {
      rows.set(r.name, {
        name: r.name,
        files: 0,
        bytes: 0,
        completed: r.completed,
        failed: r.failed,
        medianSecondsToComplete: r.medianSecondsToComplete,
      })
    }
  }
  return [...rows.values()].sort((a, b) => b.bytes - a.bytes || b.completed + b.failed - (a.completed + a.failed))
}

/**
 * Where the library's files came from, and how well each source has been delivering over the last
 * 30 days. Files/size are the all-time on-disk tally; success/wait are the queue's recent record.
 */
export function SourceReliability({
  sources,
  reliability,
}: {
  sources: SourceUsage[]
  reliability: SourceReliabilityDto[]
}) {
  const { t } = useLingui()
  const nameLabel = useNameLabel()
  const rows = useMemo(() => mergeRows(sources, reliability), [sources, reliability])

  if (rows.length === 0) {
    return (
      <Text c="var(--ink-3)" size="sm">
        {t`Nothing downloaded yet.`}
      </Text>
    )
  }

  return (
    <Table verticalSpacing={6} withRowBorders={false}>
      <Table.Thead>
        <Table.Tr>
          <Table.Th>{t`Source`}</Table.Th>
          <Table.Th style={{ textAlign: 'right' }}>{t`Files`}</Table.Th>
          <Table.Th style={{ textAlign: 'right' }}>{t`Size`}</Table.Th>
          <Table.Th style={{ textAlign: 'right' }}>{t`Success`}</Table.Th>
          <Table.Th style={{ textAlign: 'right' }}>{t`Median wait`}</Table.Th>
        </Table.Tr>
      </Table.Thead>
      <Table.Tbody>
        {rows.map((r) => {
          const attempts = r.completed + r.failed
          const successRate = attempts > 0 ? r.completed / attempts : null
          const low = successRate !== null && successRate < 0.8
          return (
            <Table.Tr key={r.name}>
              <Table.Td>
                <Text size="sm">{nameLabel(PROTOCOL_LABELS, r.name)}</Text>
              </Table.Td>
              <Table.Td style={{ textAlign: 'right' }}>
                <Text size="sm" c="var(--ink-3)" className="tnum">
                  {formatNumber(r.files)}
                </Text>
              </Table.Td>
              <Table.Td style={{ textAlign: 'right' }}>
                <Text size="sm" fw={600} className="tnum">
                  {formatBytes(r.bytes)}
                </Text>
              </Table.Td>
              <Table.Td style={{ textAlign: 'right' }}>
                {successRate === null ? (
                  <Text size="sm" c="var(--ink-3)">
                    {t`n/a`}
                  </Text>
                ) : (
                  <div>
                    <Text size="sm" fw={600} className="tnum" c={low ? 'var(--warn)' : undefined}>
                      {formatPercent(successRate)}
                    </Text>
                    <Progress
                      value={successRate * 100}
                      size={4}
                      radius="xl"
                      color={low ? 'var(--warn)' : 'var(--brand)'}
                      mt={4}
                    />
                  </div>
                )}
              </Table.Td>
              <Table.Td style={{ textAlign: 'right' }}>
                <Text size="sm" c="var(--ink-3)" className="tnum">
                  {r.medianSecondsToComplete == null ? t`n/a` : formatReadingTime(r.medianSecondsToComplete)}
                </Text>
              </Table.Td>
            </Table.Tr>
          )
        })}
      </Table.Tbody>
    </Table>
  )
}
