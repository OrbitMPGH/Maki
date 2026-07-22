import { Progress, SimpleGrid, Text, UnstyledButton } from '@mantine/core'
import type { RecommendationIndexStatus } from '../api/hooks'

const MODELS: { value: string; title: string; subtitle: string }[] = [
  { value: 'off', title: 'Off', subtitle: 'No semantic search or recommendations' },
  { value: 'base', title: 'Base', subtitle: 'Lighter · ~240 MB RAM · recommended for most people' },
  { value: 'large', title: 'Large', subtitle: 'Sharper · ~500 MB RAM · if you can spare the resources' },
]

function formatRemaining(seconds: number): string {
  if (seconds < 90) return 'about a minute left'
  const minutes = Math.round(seconds / 60)
  if (minutes < 60) return `about ${minutes} min left`
  const hours = Math.floor(minutes / 60)
  const rest = minutes % 60
  return rest === 0 ? `about ${hours} hr left` : `about ${hours} hr ${rest} min left`
}

function statusLine(status: RecommendationIndexStatus | undefined): string {
  if (!status) return '…'
  if (status.modelSwitching) return 'Switching model… downloading the model and its index.'
  if (status.embeddingModel === 'off') return 'Semantic search and recommendations are off.'

  const total = status.recommendableTotal
  if (status.running) {
    const eta =
      status.estimatedSecondsRemaining != null ? ` · ${formatRemaining(status.estimatedSecondsRemaining)}` : ''
    const fresh = status.embedded > 0 ? ` (${status.embedded.toLocaleString()} new)` : ''
    return status.phase === 'preparing'
      ? 'Preparing model…'
      : `Indexing… ${status.scanned.toLocaleString()}${total ? ` / ${total.toLocaleString()}` : ''}${fresh}${eta}`
  }
  if (!status.dumpPresent) return 'Waiting for the MangaBaka snapshot to download first.'
  if (status.vectorCount === 0) return 'No index yet — the prebuilt vectors download automatically.'

  const source = status.prebuiltInstalledAt
    ? ` Downloaded ${new Date(status.prebuiltInstalledAt).toLocaleDateString()}.`
    : status.finishedAt
      ? ` Last run ${new Date(status.finishedAt).toLocaleString()}.`
      : ''
  return `${status.vectorCount.toLocaleString()}${total ? ` / ${total.toLocaleString()}` : ''} series embedded.${source}`
}

/**
 * The three-way embedding-model picker (Off / Base / Large) plus the shared progress + status
 * line. Used by both the Settings recommendation section and the setup wizard so they look the
 * same. Selecting a tile starts a live switch; the parent owns the mutation.
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

  return (
    <>
      <SimpleGrid cols={3} spacing="sm">
        {MODELS.map((m) => {
          const active = selected === m.value
          return (
            <UnstyledButton
              key={m.value}
              disabled={disabled}
              onClick={() => onSelect(m.value)}
              aria-pressed={active}
              style={{
                padding: '12px 14px',
                borderRadius: 10,
                border: `1px solid ${active ? 'var(--brand)' : 'var(--border)'}`,
                background: active ? 'var(--surface-hover)' : 'transparent',
                boxShadow: active ? '0 0 0 1px var(--brand)' : undefined,
                opacity: disabled && !active ? 0.6 : 1,
                cursor: disabled ? 'default' : 'pointer',
              }}
            >
              <Text size="sm" fw={active ? 700 : 600}>
                {m.title}
              </Text>
              <Text size="xs" c="dimmed" mt={2}>
                {m.subtitle}
              </Text>
            </UnstyledButton>
          )
        })}
      </SimpleGrid>

      {(running || switching || pct !== null) && selected !== 'off' && (
        <Progress
          mt="sm"
          value={(running || switching) && pct === null ? 100 : (pct ?? 0)}
          animated={running || switching}
          striped={running || switching}
          color={status?.lastError ? 'red' : 'brand'}
        />
      )}

      <Text size="sm" mt="sm">
        {statusLine(status)}
      </Text>
      {status?.modelSwitchError && !switching && (
        <Text size="xs" c="red">
          Model switch: {status.modelSwitchError}
        </Text>
      )}
      {status?.lastError && (
        <Text size="xs" c="red">
          Last error: {status.lastError}
        </Text>
      )}
    </>
  )
}
