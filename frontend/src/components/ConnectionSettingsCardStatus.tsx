import { Text, type MantineSpacing } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
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

/** When the last Test ran, and why it failed if it did, under the card's fields. */
export function ConnectionTestDetail({ testKey, mt = 'sm' }: { testKey: ConnectionTestKey | undefined; mt?: MantineSpacing }) {
  const { status } = useConnectionTest(testKey)
  if (!testKey || !status) return null
  return (
    <Text component="div" size="xs" c="var(--ink-3)" mt={mt} className="connection-test-line">
      <MetaList
        inline
        items={[
          !status.ok && status.message,
          <Trans key="when">
            Tested <TimeAgo value={status.testedAt} />
          </Trans>,
        ]}
      />
    </Text>
  )
}
