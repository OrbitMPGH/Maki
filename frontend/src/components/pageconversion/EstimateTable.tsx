import { Fragment } from 'react'
import { Table, Text } from '@mantine/core'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import type { EstimateBucket, EstimateResult, PageConversionRule, PageConversionStop } from '../../api/pageConversion'
import { formatBytes, formatNumber, formatReadingTime } from '../../format'
import { STOP_LABELS } from './labels'

const COLUMNS = 5

/** One row per rule, then everything else and the series with their own stop. */
export function EstimateTable({
  result,
  rules,
  stop,
  stale,
}: {
  result: EstimateResult
  rules: readonly PageConversionRule[]
  /** The default quality, which files no rule matched take. */
  stop: PageConversionStop
  stale: boolean
}) {
  const { t } = useLingui()
  const stopLabel = t(STOP_LABELS[stop])
  // A result from before the rules last changed no longer lines up with them by position.
  const named = rules.length === result.rules.length

  return (
    <Table.ScrollContainer minWidth={560}>
      <Table verticalSpacing="xs" style={{ opacity: stale ? 0.55 : 1 }}>
        <Table.Thead>
          <Table.Tr>
            <Table.Th>
              <Trans>Rule</Trans>
            </Table.Th>
            <Table.Th ta="right">
              <Trans>Files</Trans>
            </Table.Th>
            <Table.Th ta="right">
              <Trans>Now</Trans>
            </Table.Th>
            <Table.Th ta="right">
              <Trans>Expected</Trans>
            </Table.Th>
            <Table.Th ta="right">
              <Trans>Time</Trans>
            </Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {result.rules.map((bucket, i) => {
            const rule = named ? rules[i] : undefined
            const position = i + 1
            const name = rule?.name.trim() || t`Rule ${position}`
            const detail = rule?.stop ? t(STOP_LABELS[rule.stop]) : null
            return <Row key={i} label={name} detail={detail} bucket={bucket} />
          })}
          <Row label={t`Everything else: ${stopLabel}`} bucket={result.default} />
          {result.overrides.files > 0 && <Row label={t`Series with their own stop`} bucket={result.overrides} />}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  )
}

function Row({ label, detail, bucket }: { label: string; detail?: string | null; bucket: EstimateBucket }) {
  const size = formatBytes(bucket.bytes)
  const labelCell = (
    <Table.Td>
      <Text size="sm">{label}</Text>
      {detail && (
        <Text size="xs" c="var(--ink-3)">
          {detail}
        </Text>
      )}
    </Table.Td>
  )

  if (bucket.skip) {
    return (
      <Table.Tr>
        {labelCell}
        <Table.Td colSpan={COLUMNS - 1} ta="right">
          <Text size="sm" c="var(--ink-3)">
            <Plural value={bucket.files} one={`Skipped: # file, ${size}`} other={`Skipped: # files, ${size}`} />
          </Text>
        </Table.Td>
      </Table.Tr>
    )
  }

  if (bucket.files === 0) {
    return (
      <Table.Tr>
        {labelCell}
        <Table.Td colSpan={COLUMNS - 1} ta="right">
          <Text size="sm" c="var(--ink-3)">
            <Trans>No files</Trans>
          </Text>
        </Table.Td>
      </Table.Tr>
    )
  }

  const unsampled = bucket.sampledFiles === 0
  const hardlinked = formatBytes(bucket.hardlinkedBytes)
  return (
    <Fragment>
      <Table.Tr>
        {labelCell}
        <Table.Td ta="right">{formatNumber(bucket.files)}</Table.Td>
        <Table.Td ta="right">{size}</Table.Td>
        <Table.Td ta="right">
          {unsampled ? (
            <Text size="sm" c="var(--ink-3)">
              <Trans>Not sampled</Trans>
            </Text>
          ) : (
            <Range low={bucket.expectedLow} high={bucket.expectedHigh} format={formatBytes} />
          )}
        </Table.Td>
        <Table.Td ta="right">
          {unsampled || bucket.secondsHigh <= 0 ? (
            '-'
          ) : (
            <Range low={bucket.secondsLow} high={bucket.secondsHigh} format={formatReadingTime} />
          )}
        </Table.Td>
      </Table.Tr>
      {bucket.hardlinkedBytes > 0 && (
        <Table.Tr>
          <Table.Td colSpan={COLUMNS} pt={0}>
            <Text size="xs" c="var(--ink-3)">
              <Trans>About {hardlinked} are hardlinked to torrents and stay as they are.</Trans>
            </Text>
          </Table.Td>
        </Table.Tr>
      )}
    </Fragment>
  )
}

/** "48 GB to 53 GB", or one figure when both ends read the same. */
function Range({ low, high, format }: { low: number; high: number; format: (n: number) => string }) {
  const from = format(low)
  const to = format(high)
  return <>{from === to ? from : <Trans>{from} to {to}</Trans>}</>
}
