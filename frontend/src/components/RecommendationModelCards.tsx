import { useMemo } from 'react'
import { Button, Group, Progress, Text } from '@mantine/core'
import { useDownloadPrebuiltIndex, type RecommendationIndexStatus } from '../api/hooks'
import { notifications } from '@mantine/notifications'
import { Trans, useLingui } from '@lingui/react/macro'
import { t as now, plural } from '@lingui/core/macro'
import { formatDate, formatDateTime, formatNumber } from '../format'
import { SelectCards, type SelectCardOption } from './SelectCards'

/**
 * "Large" used to be offered here too. It measured no better than Base and is now behind it, so
 * keeping it as a selection would be selling people 260 MB for nothing. Accounts still on it are
 * migrated to base automatically on the backend.
 *
 * A hook rather than a module-level table: the module evaluates once, so a rendered string here
 * would be stuck in whichever language was active at that moment.
 */
function useModelOptions(): SelectCardOption<string>[] {
  const { t } = useLingui()
  return [
    { value: 'off', title: t`Off`, subtitle: t`No semantic search or recommendations` },
    { value: 'base', title: t`On`, subtitle: t`~240 MB RAM · best results, recommended` },
  ]
}

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
 * The two-way embedding-model picker (Off / Base) plus the shared progress + status line. Used by
 * both the Settings recommendation section and the setup wizard so they look the same. Selecting a
 * tile starts a live switch; the parent owns the mutation.
 */
export function RecommendationModelCards({
  status,
  onSelect,
  busy,
}: {
  status: RecommendationIndexStatus | undefined
  onSelect: (kind: string) => void
  busy: boolean
}) {
  const selected = status?.embeddingModel ?? 'base'
  const running = status?.running ?? false
  const switching = status?.modelSwitching ?? false
  const total = status?.recommendableTotal ?? null
  const done = running ? status?.scanned ?? 0 : status?.vectorCount ?? 0
  const pct = total && total > 0 ? Math.min(100, Math.round((done / total) * 100)) : null
  const disabled = !status || busy || switching
  const download = useDownloadPrebuiltIndex()
  const off = status?.embeddingModel === 'off'
  const models = useModelOptions()
  const modelSwitchError = status?.modelSwitchError
  const lastError = status?.lastError
  // `statusLine` and `formatRemaining` are plain functions using the core macro, so they read the
  // catalogue at the moment they run rather than subscribing. `i18n.locale` in the deps is what
  // re-runs them on a language switch.
  const { i18n } = useLingui()
  const line = useMemo(() => statusLine(status), [status, i18n.locale])

  return (
    <>
      <SelectCards options={models} value={selected} onChange={onSelect} disabled={disabled} fillLeft={false} />

      {(running || switching || pct !== null) && selected !== 'off' && (
        <Progress
          mt="sm"
          value={(running || switching) && pct === null ? 100 : (pct ?? 0)}
          animated={running || switching}
          striped={running || switching}
          color={status?.lastError ? 'red' : 'brand'}
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
        <Text size="xs" c="red">
          <Trans>Model switch: {modelSwitchError}</Trans>
        </Text>
      )}
      {lastError && (
        <Text size="xs" c="red">
          <Trans>Last error: {lastError}</Trans>
        </Text>
      )}
    </>
  )
}
