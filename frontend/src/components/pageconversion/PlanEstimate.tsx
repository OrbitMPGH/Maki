import { useEffect, useRef, useState, type ReactNode } from 'react'
import { Button, Group, Progress, Stack, Text } from '@mantine/core'
import { useDebouncedValue } from '@mantine/hooks'
import { Callout } from '../ui/Callout'
import { IconRefresh } from '../../lib/icons'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import {
  estimateRequestKey,
  useEstimateMark,
  usePageConversionEstimate,
  useStartPageConversionEstimate,
  type EstimateBucket,
  type EstimateConvertedEarlier,
  type EstimatePassedOver,
  type EstimateResult,
  type PageConversionEstimate,
  type PageConversionPlanRequest,
  type PageConversionRule,
  type PageConversionSettings,
} from '../../api/pageConversion'
import { formatBytes, formatReadingTime } from '../../format'
import { useCheckedWithLabel } from './labels'
import { toRuleDtos } from './ruleTemplates'

/** How long the plan has to stay unchanged before a new estimate starts. */
const ESTIMATE_DEBOUNCE_MS = 1500

export interface PlanEstimate {
  estimate: PageConversionEstimate | undefined
  /** The last finished result, kept up while a newer one runs. */
  result: EstimateResult | null
  running: boolean
  starting: boolean
  /** The estimate failed or was cancelled. */
  broken: boolean
  /** The estimate could not be started or read. */
  startFailed: boolean
  /** The result is from another plan or another run than the one on screen. */
  stale: boolean
  restart: () => void
}

/**
 * What the plan on screen would do to the library, estimated on the server by encoding a sample of
 * each row's files. A new estimate starts once the plan has settled, unless `paused`.
 */
export function usePlanEstimate(value: PageConversionSettings, rules: PageConversionRule[], paused: boolean): PlanEstimate {
  const { data: estimate, isSuccess, isError, refetch } = usePageConversionEstimate()
  const { data: mark } = useEstimateMark()
  const start = useStartPageConversionEstimate()
  const startedFor = useRef<string | null>(null)
  const [shown, setShown] = useState<PageConversionEstimate | null>(null)

  const plan: PageConversionPlanRequest = { settings: value, rules: toRuleDtos(rules) }
  const requestKey = estimateRequestKey(plan)
  const [settledKey] = useDebouncedValue(requestKey, ESTIMATE_DEBOUNCE_MS)
  // The estimate on the server is ours when this tab started it for this very request.
  const forThisPlan = !!estimate && mark?.requestKey === requestKey && mark.planHash === estimate.planHash

  const { mutate: startEstimate } = start
  useEffect(() => {
    if (!value.format || !isSuccess || paused || settledKey !== requestKey || startedFor.current === requestKey) return
    startedFor.current = requestKey
    if (!forThisPlan) startEstimate({ settings: value, rules: toRuleDtos(rules) })
  }, [value, rules, requestKey, settledKey, isSuccess, paused, forThisPlan, startEstimate])

  // A re-estimate answers without a result until it finishes; the last one stays up until then.
  if (estimate?.result && estimate !== shown) setShown(estimate)

  const running = estimate?.state === 'running' || start.isPending
  const broken = estimate?.state === 'failed' || estimate?.state === 'cancelled'
  const result = shown?.result ?? null
  return {
    estimate,
    result,
    running,
    starting: start.isPending,
    broken,
    startFailed: isError || start.isError,
    stale: !!result && (running || !forThisPlan || broken),
    restart: () => {
      if (isError && !start.isError) {
        void refetch()
        return
      }
      startedFor.current = requestKey
      startEstimate(plan)
    },
  }
}

/** The convert buckets added up: everything the pass would rewrite. */
export function estimateTotals(result: EstimateResult) {
  const buckets = [...result.rules, result.default, result.overrides].filter((b) => !b.skip && b.files > 0)
  const sum = (pick: (b: EstimateBucket) => number) => buckets.reduce((n, b) => n + pick(b), 0)
  return {
    files: sum((b) => b.files),
    bytes: sum((b) => b.bytes),
    expectedLow: sum((b) => b.expectedLow),
    expectedHigh: sum((b) => b.expectedHigh),
    secondsLow: sum((b) => b.secondsLow),
    secondsHigh: sum((b) => b.secondsHigh),
    sampled: buckets.some((b) => b.sampledFiles > 0),
  }
}

/** "48 GB to 53 GB" as text, or one figure when both ends read the same. */
export function useRangeText() {
  const { t } = useLingui()
  return (low: number, high: number, format: (n: number) => string): string => {
    const from = format(low)
    const to = format(high)
    return from === to ? from : t`${from} to ${to}`
  }
}

/** "48 GB to 53 GB", or one figure when both ends read the same. */
export function Range({ low, high, format }: { low: number; high: number; format: (n: number) => string }) {
  const from = format(low)
  const to = format(high)
  return <>{from === to ? from : <Trans>{from} to {to}</Trans>}</>
}

/** What the pass passes over and what was converted earlier, with errors and a way to run it again. */
export function EstimateDetails({ plan }: { plan: PlanEstimate }) {
  const { estimate, result, running, broken, startFailed, stale, starting, restart } = plan

  return (
    <Stack gap="md">
      <Text size="sm" c="var(--ink-3)">
        <Trans>
          Sizes come from encoding a sample of the files each row covers. Nothing is changed. Times depend on how busy
          this server is.
        </Trans>
      </Text>

      {startFailed && !running && (
        <Callout tone="danger" role="alert" title={<Trans>The estimate could not be started</Trans>}>
          <Button variant="default" size="xs" leftSection={<IconRefresh size={16} />} onClick={restart}>
            <Trans>Try again</Trans>
          </Button>
        </Callout>
      )}

      {!startFailed && broken && !running && (
        <Callout
          tone="danger"
          role="alert"
          title={
            estimate?.state === 'cancelled' ? <Trans>The estimate was cancelled</Trans> : <Trans>The estimate failed</Trans>
          }
        >
          <Stack gap="xs" align="flex-start">
            {estimate?.state === 'failed' && (
              <Text size="sm">
                <Trans>The estimate failed on this server. Try again, or check the log.</Trans>
              </Text>
            )}
            <Button variant="default" size="xs" leftSection={<IconRefresh size={16} />} onClick={restart}>
              <Trans>Try again</Trans>
            </Button>
          </Stack>
        </Callout>
      )}

      {running && <Sampling estimate={estimate} starting={starting} />}

      {result && (
        <>
          <Overrides bucket={result.overrides} stale={stale} />
          <PassedOver passed={result.passedOver} stale={stale} />
          <ConvertedEarlier lines={result.convertedEarlier} inTrash={result.convertedWithOriginalInTrash} stale={stale} />
          <Group>
            <Button variant="default" size="xs" leftSection={<IconRefresh size={16} />} disabled={running} onClick={restart}>
              <Trans>Re-estimate</Trans>
            </Button>
          </Group>
        </>
      )}
    </Stack>
  )
}

export function Sampling({ estimate, starting }: { estimate: PageConversionEstimate | undefined; starting: boolean }) {
  const { t } = useLingui()
  const done = starting ? 0 : (estimate?.done ?? 0)
  const total = starting ? 0 : (estimate?.total ?? 0)
  return (
    <Stack gap={6}>
      <Text size="sm" aria-live="polite">
        {starting ? (
          <Trans>Starting the estimate</Trans>
        ) : total > 0 ? (
          <Plural value={total} one={`Sampling ${done} of # file`} other={`Sampling ${done} of # files`} />
        ) : (
          <Trans>Counting the files to sample</Trans>
        )}
      </Text>
      <Progress value={total > 0 ? (done / total) * 100 : 0} aria-label={t`Estimate progress`} animated={total === 0} />
    </Stack>
  )
}

function Overrides({ bucket, stale }: { bucket: EstimateBucket; stale: boolean }) {
  const { t } = useLingui()
  const range = useRangeText()
  if (bucket.files === 0) return null
  const size = formatBytes(bucket.bytes)
  const expected = range(bucket.expectedLow, bucket.expectedHigh, formatBytes)
  const time = range(bucket.secondsLow, bucket.secondsHigh, formatReadingTime)
  return (
    <Stack gap={4} style={{ opacity: stale ? 0.55 : 1 }}>
      <Text size="sm" fw={500}>
        <Trans>Series with their own setting</Trans>
      </Text>
      <Text size="sm">
        <Plural
          value={bucket.files}
          one={`# file, ${size}, converted at the quality set in its series menu.`}
          other={`# files, ${size}, converted at the quality set in their series menus.`}
        />
        {bucket.sampledFiles > 0 && ' '}
        {bucket.sampledFiles > 0 &&
          (bucket.secondsHigh > 0 ? t`${expected} after, about ${time}.` : t`${expected} after.`)}
      </Text>
    </Stack>
  )
}

function PassedOver({ passed, stale }: { passed: EstimatePassedOver; stale: boolean }) {
  const hardlinked = formatBytes(passed.hardlinkedBytes)
  const skipped = formatBytes(passed.skippedByRuleBytes)
  const never = formatBytes(passed.neverBytes)
  const checked = formatBytes(passed.alreadyCheckedBytes)
  const { neverSeries } = passed
  const any =
    passed.hardlinkedFiles +
      passed.cleanupDue +
      passed.alreadyCompact +
      passed.downloadInFlight +
      passed.pendingImport +
      passed.skippedByRule +
      passed.neverFiles +
      passed.undone +
      passed.alreadyChecked >
    0
  if (!any) return null

  return (
    <Stack gap={4} style={{ opacity: stale ? 0.55 : 1 }}>
      <Text size="sm" fw={500}>
        <Trans>Passed over</Trans>
      </Text>
      <Stack component="ul" gap={2} m={0} pl="md">
        {passed.hardlinkedFiles > 0 && (
          <Line>
            <Plural
              value={passed.hardlinkedFiles}
              one={`# file is hardlinked to a torrent and stays as it is (${hardlinked})`}
              other={`# files are hardlinked to torrents and stay as they are (${hardlinked})`}
            />
          </Line>
        )}
        {passed.cleanupDue > 0 && (
          <Line>
            <Plural
              value={passed.cleanupDue}
              one="# file is due for read-file cleanup"
              other="# files are due for read-file cleanup"
            />
          </Line>
        )}
        {passed.alreadyCompact > 0 && (
          <Line>
            <Plural
              value={passed.alreadyCompact}
              one="# file is already WebP, AVIF or JPEG XL"
              other="# files are already WebP, AVIF or JPEG XL"
            />
          </Line>
        )}
        {passed.downloadInFlight > 0 && (
          <Line>
            <Plural
              value={passed.downloadInFlight}
              one="# file is in a series with a download in flight"
              other="# files are in series with a download in flight"
            />
          </Line>
        )}
        {passed.pendingImport > 0 && (
          <Line>
            <Plural
              value={passed.pendingImport}
              one="# file is waiting on an import link"
              other="# files are waiting on an import link"
            />
          </Line>
        )}
        {passed.skippedByRule > 0 && (
          <Line>
            <Plural
              value={passed.skippedByRule}
              one={`# file is skipped by a rule (${skipped})`}
              other={`# files are skipped by a rule (${skipped})`}
            />
          </Line>
        )}
        {passed.neverFiles > 0 && (
          <Line>
            <Plural
              value={passed.neverFiles}
              one={`# file is in a series set to Never (${never})`}
              other={`# files are in ${neverSeries} series set to Never (${never})`}
            />
          </Line>
        )}
        {passed.undone > 0 && (
          <Line>
            <Plural
              value={passed.undone}
              one="# file was put back by an undo and stays as it is"
              other="# files were put back by an undo and stay as they are"
            />
          </Line>
        )}
        {passed.alreadyChecked > 0 && (
          <Line>
            <Plural
              value={passed.alreadyChecked}
              one={`# file was already checked with these settings (${checked})`}
              other={`# files were already checked with these settings (${checked})`}
            />
          </Line>
        )}
      </Stack>
    </Stack>
  )
}

function ConvertedEarlier({
  lines,
  inTrash,
  stale,
}: {
  lines: EstimateConvertedEarlier[]
  inTrash: number
  stale: boolean
}) {
  const label = useCheckedWithLabel()
  if (lines.length === 0) return null

  return (
    <Stack gap={4} style={{ opacity: stale ? 0.55 : 1 }}>
      <Text size="sm" fw={500}>
        <Trans>Converted earlier</Trans>
      </Text>
      <Text size="xs" c="var(--ink-3)">
        <Trans>These are never converted again.</Trans>
      </Text>
      <Stack component="ul" gap={2} m={0} pl="md">
        {lines.map((line) => {
          const settings = label(line.checkedWith)
          const saved = formatBytes(line.savedBytes)
          return (
            <Line key={line.checkedWith}>
              <Plural
                value={line.files}
                one={`${settings}: # file, ${saved} saved`}
                other={`${settings}: # files, ${saved} saved`}
              />
            </Line>
          )
        })}
      </Stack>
      {inTrash > 0 && (
        <Text size="sm" c="var(--ink-3)" pt={4}>
          <Plural
            value={inTrash}
            one="# of them still has its original in the trash. Undo it from the series page."
            other="# of them still have their original in the trash. Undo them from the series page."
          />
        </Text>
      )}
    </Stack>
  )
}

function Line({ children }: { children: ReactNode }) {
  return (
    <Text component="li" size="sm">
      {children}
    </Text>
  )
}
