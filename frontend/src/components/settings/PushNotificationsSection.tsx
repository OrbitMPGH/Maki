import { Text } from '@mantine/core'
import { Trans } from '@lingui/react/macro'
import { SettingsSection } from '../../pages/settings/SettingsSection'
import { pwaSupported } from '../../lib/pwa/serviceWorker'
import { SecureContextNote } from './SecureContextNote'

export function PushNotificationsSection() {
  return (
    <SettingsSection
      id="push-notifications"
      title={<Trans>Push notifications</Trans>}
      description={<Trans>Notifications on this device, even when Maki is closed.</Trans>}
    >
      {pwaSupported() ? (
        <Text size="sm" c="var(--ink-3)">
          <Trans>Push notifications are not set up on this device.</Trans>
        </Text>
      ) : (
        <SecureContextNote />
      )}
    </SettingsSection>
  )
}
