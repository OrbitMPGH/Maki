import { Text, type MantineSpacing } from '@mantine/core'
import { useLingui } from '@lingui/react/macro'
import { useConnectionTest, type ConnectionTestKey, type ConnectionTestStatus } from '../api/hooks'
import { MetaList } from './ui/MetaList'
import { StatusDot } from './ui/StatusDot'
import { TimeAgo } from './ui/TimeAgo'

/**
 * Where a connection stands, as a dot and a word for the card's head: the same words Trackers and
 * notification connections use. Says nothing until the test results load.
 */
export function ConnectionStatus({
  testKey,
  configured = true,
}: {
  testKey: ConnectionTestKey | undefined
  /** False while the connection has no address saved yet. */
  configured?: boolean
}) {
  const { status, loaded } = useConnectionTest(testKey)
  if (!loaded || !testKey) return null
  return <ConnectionStatusView status={status} configured={configured} />
}

export function ConnectionStatusView({
  status,
  configured = true,
}: {
  status: ConnectionTestStatus | null
  configured?: boolean
}) {
  const { t } = useLingui()
  const [tone, label] = !configured
    ? ['neutral', t`Not set up`]
    : !status
      ? ['neutral', t`Not tested`]
      : status.ok
        ? ['ok', t`Connected`]
        : ['danger', t`Failing`]
  return (
    <StatusDot tone={tone} role="status">
      {label}
    </StatusDot>
  )
}

/** Why the last Test failed and when, under the card's fields. Nothing while the connection works. */
export function ConnectionTestFailure({ testKey, mt = 'sm' }: { testKey: ConnectionTestKey | undefined; mt?: MantineSpacing }) {
  const { status } = useConnectionTest(testKey)
  if (!testKey || !status || status.ok) return null
  return (
    <Text component="div" size="xs" c="var(--ink-3)" mt={mt} className="connection-test-line">
      <MetaList inline items={[status.message, <TimeAgo key="when" value={status.testedAt} />]} />
    </Text>
  )
}
