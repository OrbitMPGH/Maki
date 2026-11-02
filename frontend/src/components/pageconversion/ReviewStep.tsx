import { useEffect, useRef, useState, type ReactNode } from 'react'
import { Alert, Button, Group, Progress, Skeleton, Stack, Text } from '@mantine/core'
import { IconRefresh } from '@tabler/icons-react'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import {
  estimateRequestKey,
  useEstimateMark,
  usePageConversionEstimate,
  useStartPageConversionEstimate,
  type EstimateConvertedEarlier,
  type EstimatePassedOver,
  type PageConversionEstimate,
  type PageConversionPlanRequest,
  type PageConversionRule,
  type PageConversionSettings,
} from '../../api/pageConversion'
import { formatBytes } from '../../format'
import { EstimateTable } from './EstimateTable'
import { useCheckedWithLabel } from './labels'
import { toRuleDtos } from './ruleTemplates'

/**
 * Step 4: what the pass would do to the library, estimated by encoding a sample of the files each
 * rule covers. Starts on its own, and again whenever the choices behind it changed since the last run.
 */
export function ReviewStep({ value, rules }: { value: PageConversionSettings; rules: PageConversionRule[] }) {
  const { data: estimate, isSuccess, isError, refetch } = usePageConversionEstimate()
  const { data: mark } = useEstimateMark()
  const start = useStartPageConversionEstimate()
  const startedFor = useRef<string | null>(null)
  const [shown, setShown] = useState<PageConversionEstimate | null>(null)

  const plan: PageConversionPlanRequest = { settings: value, rules: toRuleDtos(rules) }
  const requestKey = estimateRequestKey(plan)
  // The estimate on the server is ours when this tab started it for this very request.
  const forThisPlan = !!estimate && mark?.requestKey === requestKey && mark.planHash === estimate.planHash

  const { mutate: startEstimate } = start
  useEffect(() => {
    if (!value.format || !isSuccess || startedFor.current === requestKey) return
    startedFor.current = requestKey
    if (!forThisPlan) startEstimate({ settings: value, rules: toRuleDtos(rules) })
  }, [value, rules, requestKey, isSuccess, forThisPlan, startEstimate])

  // A re-estimate answers without a result until it finishes; the last one stays up until then.
  if (estimate?.result && estimate !== shown) setShown(estimate)

  const restart = () => {
    startedFor.current = requestKey
    startEstimate(plan)
  }

  if (!value.format) {
    return (
      <Text size="sm" c="var(--ink-3)">
        <Trans>Conversion is off, so there is nothing to estimate. Continue to save that choice.</Trans>
      </Text>
    )
  }

  const running = estimate?.state === 'running' || start.isPending
  const broken = estimate?.state === 'failed' || estimate?.state === 'cancelled'
  const result = shown?.result ?? null
  const stale = !!result && (running || !forThisPlan || broken)
  const startFailed = isError || start.isError

  return (
    <Stack gap="md">
      <Text size="sm" c="var(--ink-3)">
        <Trans>
          Sizes come from encoding a sample of the files each rule covers. Nothing is changed. Times depend on how busy
          this server is.
        </Trans>
      </Text>

      {startFailed && !running && (
        <Alert color="red" role="alert" title={<Trans>The estimate could not be started</Trans>}>
          <Button
            variant="default"
            size="xs"
            leftSection={<IconRefresh size={14} />}
            onClick={() => (start.isError ? restart() : void refetch())}
          >
            <Trans>Try again</Trans>
          </Button>
        </Alert>
      )}

      {!startFailed && broken && !running && (
        <Alert
          color="red"
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
            <Button variant="default" size="xs" leftSection={<IconRefresh size={14} />} onClick={restart}>
              <Trans>Try again</Trans>
            </Button>
          </Stack>
        </Alert>
      )}

      {running && <Sampling estimate={estimate} starting={start.isPending} />}

      {!result && !running && !broken && !startFailed && (
        <Stack gap="sm">
          <Skeleton height={20} width="50%" />
          <Skeleton height={120} />
        </Stack>
      )}

      {result && (
        <>
          <EstimateTable result={result} rules={rules} stop={value.stop} stale={stale} />
          <Group>
            <Button variant="default" size="xs" leftSection={<IconRefresh size={14} />} disabled={running} onClick={restart}>
              <Trans>Re-estimate</Trans>
            </Button>
          </Group>
          <PassedOver passed={result.passedOver} stale={stale} />
          <ConvertedEarlier lines={result.convertedEarlier} inTrash={result.convertedWithOriginalInTrash} stale={stale} />
        </>
      )}
    </Stack>
  )
}

function Sampling({ estimate, starting }: { estimate: PageConversionEstimate | undefined; starting: boolean }) {
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
      <Progress
        value={total > 0 ? (done / total) * 100 : 0}
        size="sm"
        aria-label={t`Estimate progress`}
        animated={total === 0}
      />
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
              one="# file is already WebP or AVIF"
              other="# files are already WebP or AVIF"
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
