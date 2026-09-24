import { Button, Stack, Text, ThemeIcon } from '@mantine/core'
import { IconAlertTriangle, IconCards, IconSparkles } from '@tabler/icons-react'
import { Trans, useLingui } from '@lingui/react/macro'
import { useNavigate } from 'react-router-dom'

/**
 * What the queue shows instead of cards: no seeds yet (cold start, trending only), everything
 * drained (exhausted), or the request itself failed (most often the missing local MangaBaka dump).
 */
export function QueueEmptyState({
  kind,
  error,
  onRetry,
  retrying,
}: {
  kind: 'coldStart' | 'exhausted' | 'error'
  error?: unknown
  onRetry?: () => void
  retrying?: boolean
}) {
  const { t } = useLingui()
  const navigate = useNavigate()

  if (kind === 'error') {
    return (
      <Stack className="queue-empty" align="center" gap="sm" py="xl">
        <ThemeIcon size={48} radius="xl" variant="light" color="var(--warn)">
          <IconAlertTriangle size={24} />
        </ThemeIcon>
        <Text fw={600}><Trans>Couldn't load the queue</Trans></Text>
        <Text size="sm" c="var(--ink-3)" ta="center" maw={420}>{String(error)}</Text>
        {onRetry && (
          <Button variant="default" mt="sm" loading={retrying} onClick={onRetry}>
            {t`Try again`}
          </Button>
        )}
      </Stack>
    )
  }

  if (kind === 'coldStart') {
    return (
      <Stack className="queue-empty" align="center" gap="sm" py="xl">
        <ThemeIcon size={48} radius="xl" variant="light" color="var(--brand)">
          <IconSparkles size={24} />
        </ThemeIcon>
        <Text fw={600}><Trans>Starting with what's trending</Trans></Text>
        <Text size="sm" c="var(--ink-3)" ta="center" maw={420}>
          <Trans>Save a few titles and the queue turns personal within a few minutes.</Trans>
        </Text>
      </Stack>
    )
  }

  return (
    <Stack className="queue-empty" align="center" gap="sm" py="xl">
      <ThemeIcon size={48} radius="xl" variant="light" color="var(--ok)">
        <IconCards size={24} />
      </ThemeIcon>
      <Text fw={600}><Trans>You're caught up</Trans></Text>
      <Text size="sm" c="var(--ink-3)" ta="center" maw={420}>
        <Trans>Skipped titles come back in 30 days.</Trans>
      </Text>
      <Button variant="default" mt="sm" onClick={() => navigate('/discover/taste')}>
        {t`Explore your taste`}
      </Button>
      {onRetry && (
        <Button variant="subtle" size="sm" loading={retrying} onClick={onRetry}>
          {t`Check again`}
        </Button>
      )}
    </Stack>
  )
}
