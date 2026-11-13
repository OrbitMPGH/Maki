import { Text, type MantineSpacing } from '@mantine/core'
import { Trans } from '@lingui/react/macro'
import { useConnectionTest, type ConnectionTestKey, type ConnectionTestStatus } from '../api/hooks'
import { MetaList } from './ui/MetaList'
import { StatusDot } from './ui/StatusDot'
import { TimeAgo } from './ui/TimeAgo'

/** Where a connection's last Test left it, under the card's fields. Says nothing until the results load. */
export function ConnectionTestLine({ testKey, mt }: { testKey: ConnectionTestKey | undefined; mt?: MantineSpacing }) {
  const { status, loaded } = useConnectionTest(testKey)
  if (!loaded || !testKey) return null
  return <ConnectionTestLineView status={status} mt={mt} />
}

export function ConnectionTestLineView({
  status,
  mt = 'sm',
}: {
  status: ConnectionTestStatus | null
  mt?: MantineSpacing
}) {
  const label = !status ? (
    <StatusDot tone="neutral">
      <Trans>Not tested</Trans>
    </StatusDot>
  ) : status.ok ? (
    <StatusDot tone="ok">
      <Trans>Connected</Trans>
    </StatusDot>
  ) : (
    <StatusDot tone="danger">
      <Trans>Failed</Trans>
    </StatusDot>
  )

  return (
    <Text component="div" size="xs" c="var(--ink-3)" mt={mt} className="connection-test-line" role="status">
      <MetaList
        inline
        items={[
          label,
          status && !status.ok && status.message,
          status && <TimeAgo key="when" value={status.testedAt} />,
        ]}
      />
    </Text>
  )
}
