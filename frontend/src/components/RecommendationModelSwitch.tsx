import { useMemo } from 'react'
import { Button, Group, Progress, Switch, Text } from '@mantine/core'
import { useDownloadPrebuiltIndex, type RecommendationIndexStatus } from '../api/hooks'
import { notifications } from '@mantine/notifications'
import { Trans, useLingui } from '@lingui/react/macro'
import { t as now, plural } from '@lingui/core/macro'
import { formatDate, formatDateTime, formatNumber } from '../format'

/**
 * "Large" used to be offered here too, as a third card. It measured no better than Base, so it went,
 * and with only Off and Base left the picker is a switch. Accounts still on Large are migrated to
 * base automatically on the backend.
 */
const ON_MODEL = 'base'

/** Called fresh on every render, so the core macros read the active catalogue each time. */
function formatRemaining(seconds: number): string {
  if (seconds < 90) return now`about a minute left`
  const minutes = Math.round(seconds / 60)
  if (minutes < 60) return plural(minutes, { one: 'about # min left', other: 'about # min left' })
  const hours = Math.floor(minutes / 60)
  const rest = minutes % 60
  if (rest === 0) return plural(hours, { one: 'about # hr left', other: 'about # hr left' })
  const hourPhrase = plural(hours, { one: '# hr', other: '# hr' })
  const restPhrase = plural(rest, { one: '# min', other: '# min' })
  return now`about ${hourPhrase} ${restPhrase} left`
}

function statusLine(status: RecommendationIndexStatus | undefined): string {
  if (!status) return '…'
  if (status.modelSwitching) return now`Switching model… downloading the model and its index.`
  if (status.embeddingModel === 'off') return now`Semantic search and recommendations are off.`

  const total = status.recommendableTotal
  if (status.running) {
    const remaining = status.estimatedSecondsRemaining
    const etaPart = remaining != null ? ` · ${formatRemaining(remaining)}` : ''
    const embedded = status.embedded
    const freshPart = embedded > 0 ? ` (${plural(embedded, { one: '# new', other: '# new' })})` : ''
    const scanned = formatNumber(status.scanned)
    const totalPart = total ? ` / ${formatNumber(total)}` : ''
    return status.phase === 'preparing'
      ? now`Preparing model…`
      : now`Indexing… ${scanned}${totalPart}${freshPart}${etaPart}`
  }
  if (!status.dumpPresent) return now`Waiting for the MangaBaka snapshot to download first.`
  if (status.vectorCount === 0) return now`No index yet, the prebuilt vectors download automatically.`

  const vectorCount = status.vectorCount
  const embeddedSentence = plural(vectorCount, { one: '# series embedded.', other: '# series embedded.' })
  const installedDate = status.prebuiltInstalledAt ? formatDate(status.prebuiltInstalledAt) : null
  const finishedDate = status.finishedAt ? formatDateTime(status.finishedAt) : null
  const sourceSentence = installedDate
    ? now`Downloaded ${installedDate}.`
    : finishedDate
      ? now`Last run ${finishedDate}.`
      : ''
  return sourceSentence ? `${embeddedSentence} ${sourceSentence}` : embeddedSentence
}

/**
 * The embedding-model switch (Off / Base) plus the shared progress + status line. Used by both the
 * Settings recommendation section and the setup wizard so they look the same. Flipping it starts a
 * live model switch; the parent owns the mutation.
 */
export function RecommendationModelSwitch({
  status,
  onSelect,
  busy,
}: {
  status: RecommendationIndexStatus | undefined
  onSelect: (kind: string) => void
  busy: boolean
}) {
  const selected = status?.embeddingModel ?? ON_MODEL
  const running = status?.running ?? false
  const switching = status?.modelSwitching ?? false
  const total = status?.recommendableTotal ?? null
  const done = running ? status?.scanned ?? 0 : status?.vectorCount ?? 0
  const pct = total && total > 0 ? Math.min(100, Math.round((done / total) * 100)) : null
  const disabled = !status || busy || switching
  const download = useDownloadPrebuiltIndex()
  const off = status?.embeddingModel === 'off'
  const modelSwitchError = status?.modelSwitchError
  const lastError = status?.lastError
  // `statusLine` and `formatRemaining` are plain functions using the core macro, so they read the
  // catalogue at the moment they run rather than subscribing. `i18n.locale` in the deps is what
  // re-runs them on a language switch.
  const { t, i18n } = useLingui()
  const line = useMemo(() => statusLine(status), [status, i18n.locale])

  return (
    <>
      <Switch
        label={t`Semantic search and recommendations`}
        description={t`Uses about 240 MB of RAM.`}
        checked={selected !== 'off'}
        disabled={disabled}
        onChange={(e) => onSelect(e.currentTarget.checked ? ON_MODEL : 'off')}
      />

      {(running || switching || pct !== null) && selected !== 'off' && (
        <Progress
          mt="sm"
          value={(running || switching) && pct === null ? 100 : (pct ?? 0)}
          animated={running || switching}
          striped={running || switching}
          color={status?.lastError ? 'var(--danger)' : 'brand'}
        />
      )}

      <Group justify="space-between">
        <Text size="sm" mt="sm">
          {line}
        </Text>
        <Button
          variant="default"
          size="xs"
          mt="sm"
          loading={download.isPending}
          disabled={running || download.isPending || off}
          onClick={() =>
            download.mutate(undefined, {
              onSuccess: (r) =>
                notifications.show({
                  // "already current" and "built for a different model" are both non-installs,
                  // and the user needs to tell them apart.
                  message: r.installed
                    ? r.rowCount != null
                      ? plural(r.rowCount, {
                          one: 'Downloaded # embedded series',
                          other: 'Downloaded # embedded series',
                        })
                      : now`Downloaded the prebuilt index`
                    : r.reason,
                  color: r.installed ? 'green' : 'yellow',
                }),
              onError: (e) => notifications.show({ message: String(e), color: 'red' }),
            })
          }
        >
          {download.isPending ? <Trans>Downloading…</Trans> : <Trans>Check for prebuilt now</Trans>}
        </Button>
      </Group>
      {modelSwitchError && !switching && (
        <Text size="xs" c="var(--danger)">
          <Trans>Model switch: {modelSwitchError}</Trans>
        </Text>
      )}
      {lastError && (
        <Text size="xs" c="var(--danger)">
          <Trans>Last error: {lastError}</Trans>
        </Text>
      )}
    </>
  )
}
