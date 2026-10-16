import { Anchor, Group, Text } from '@mantine/core'
import { IconCloudOff } from '@tabler/icons-react'
import { Trans } from '@lingui/react/macro'
import { Link } from 'react-router-dom'
import { useConnectivity } from '../lib/connectivity'
import { pwaSupported } from '../lib/pwa/serviceWorker'

/** Over plain HTTP nothing is saved on the device, so there is nothing for this to point at. */
export function OfflineBanner() {
  const connectivity = useConnectivity()
  if (connectivity !== 'offline' || !pwaSupported()) return null
  return (
    <Group
      gap="xs"
      wrap="nowrap"
      role="status"
      px="md"
      py={6}
      mb="md"
      style={{
        background: 'var(--warn-soft)',
        color: 'var(--ink-hi)',
        borderRadius: 'var(--radius-control)',
      }}
    >
      <IconCloudOff size={16} stroke={1.75} color="var(--warn)" aria-hidden style={{ flexShrink: 0 }} />
      <Text size="sm" style={{ flex: 1, minWidth: 0 }}>
        <Trans>You're offline. Showing what's saved on this device.</Trans>
      </Text>
      <Anchor component={Link} to="/settings?tab=reading&s=offline-reading" size="sm" style={{ flexShrink: 0 }}>
        <Trans>Offline reading</Trans>
      </Anchor>
    </Group>
  )
}
