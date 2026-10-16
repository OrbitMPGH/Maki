import { Text } from '@mantine/core'
import { Trans } from '@lingui/react/macro'
import { SettingsSection } from '../../pages/settings/SettingsSection'
import { pwaSupported } from '../../lib/pwa/serviceWorker'
import { SecureContextNote } from './SecureContextNote'

export function OfflineReadingSection() {
  return (
    <SettingsSection
      id="offline-reading"
      title={<Trans>Offline reading</Trans>}
      description={<Trans>Chapters saved on this device, to read without a connection.</Trans>}
    >
      {pwaSupported() ? (
        <Text size="sm" c="var(--ink-3)">
          <Trans>Nothing is saved on this device yet.</Trans>
        </Text>
      ) : (
        <SecureContextNote />
      )}
    </SettingsSection>
  )
}
