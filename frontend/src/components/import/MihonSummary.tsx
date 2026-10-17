import { Button, Group, Stack, Text } from '@mantine/core'
import { Link } from 'react-router-dom'
import { Plural, Trans } from '@lingui/react/macro'
import type { MihonImportResult } from '../../api/mihonImport'
import { Panel } from '../ui/Panel'

export interface MihonOutcomeRow extends MihonImportResult {
  title: string
}

export function MihonSummary({ results, onReset }: { results: MihonOutcomeRow[]; onReset: () => void }) {
  const created = results.filter((r) => r.outcome === 'created').length
  const existing = results.filter((r) => r.outcome === 'existing').length
  const skipped = results.filter((r) => r.outcome === 'skipped')
  const failed = results.filter((r) => r.outcome === 'failed')
  const withProblems = results.filter((r) => (r.outcome === 'created' || r.outcome === 'existing') && r.reason)

  return (
    <Panel>
      <Stack gap="sm">
        <Text fw={600}>
          <Trans>Import finished</Trans>
        </Text>
        <Group gap="lg">
          <Text size="sm" c="var(--ok)">
            <Plural value={created} one="# series added" other="# series added" />
          </Text>
          <Text size="sm">
            <Plural value={existing} one="# already in the library" other="# already in the library" />
          </Text>
          <Text size="sm" c={skipped.length > 0 ? 'var(--warn)' : undefined}>
            <Plural value={skipped.length} one="# skipped" other="# skipped" />
          </Text>
          <Text size="sm" c={failed.length > 0 ? 'var(--danger)' : undefined}>
            <Plural value={failed.length} one="# failed" other="# failed" />
          </Text>
        </Group>
        <Text size="xs" c="var(--ink-3)">
          <Trans>
            New series find their sources in the background. Chapters you read in Mihon are marked as soon as the
            series has a chapter list.
          </Trans>
        </Text>
        {withProblems.length > 0 && (
          <Text size="sm" c="var(--warn)" fw={600}>
            <Plural
              value={withProblems.length}
              one="# series had problems applying tags or read marks"
              other="# series had problems applying tags or read marks"
            />
          </Text>
        )}
        {[...withProblems, ...skipped, ...failed].map((r) => {
          const { title, reason } = r
          return (
            <Text
              key={`${r.outcome}-${r.providerId}`}
              size="xs"
              c={r.outcome === 'failed' ? 'var(--danger)' : 'var(--warn)'}
              style={{ overflowWrap: 'anywhere' }}
            >
              {reason ? <Trans>{title}: {reason}</Trans> : title}
            </Text>
          )
        })}
        <Group>
          <Button component={Link} to="/library">
            <Trans>Open library</Trans>
          </Button>
          <Button variant="default" onClick={onReset}>
            <Trans>Import another backup</Trans>
          </Button>
        </Group>
      </Stack>
    </Panel>
  )
}
