import { useState } from 'react'
import { ActionIcon, Alert, Anchor, Group, Text } from '@mantine/core'
import { IconRocket, IconX } from '@tabler/icons-react'
import { Trans, useLingui } from '@lingui/react/macro'
import { useUpdateStatus } from '../api/hooks'

export default function UpdateBanner() {
  const { data } = useUpdateStatus()
  const [dismissedVersion, setDismissedVersion] = useState<string | null>(null)
  const { t } = useLingui()

  if (!data?.updateAvailable || !data.latestVersion) return null
  if (dismissedVersion === data.latestVersion) return null

  const { latestVersion, currentVersion, releaseUrl, isDocker } = data

  return (
    <Alert
      icon={<IconRocket size={18} />}
      color="brand"
      variant="light"
      radius="md"
      mb="lg"
      styles={{ message: { width: '100%' } }}
    >
      <Group justify="space-between" wrap="nowrap" gap="md">
        <Text size="sm">
          <Trans>Maki {latestVersion} is available, you're running {currentVersion}.</Trans>
          {' '}
          {releaseUrl && (
            <Anchor href={releaseUrl} target="_blank" rel="noreferrer" size="sm">
              <Trans>View changelog</Trans>
            </Anchor>
          )}
          {' '}
          {isDocker ? (
            <Trans>Pull the new image and recreate the container to update.</Trans>
          ) : (
            <Trans>Pull the latest code and rebuild to update.</Trans>
          )}
        </Text>
        <ActionIcon
          variant="subtle"
          color="gray"
          size="sm"
          aria-label={t`Dismiss`}
          onClick={() => setDismissedVersion(latestVersion)}
        >
          <IconX size={16} />
        </ActionIcon>
      </Group>
    </Alert>
  )
}
