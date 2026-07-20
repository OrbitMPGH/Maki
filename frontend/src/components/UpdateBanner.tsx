import { useState } from 'react'
import { ActionIcon, Alert, Anchor, Group, Text } from '@mantine/core'
import { IconRocket, IconX } from '@tabler/icons-react'
import { useUpdateStatus } from '../api/hooks'

export default function UpdateBanner() {
  const { data } = useUpdateStatus()
  const [dismissedVersion, setDismissedVersion] = useState<string | null>(null)

  if (!data?.updateAvailable || !data.latestVersion) return null
  if (dismissedVersion === data.latestVersion) return null

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
          Maki {data.latestVersion} is available — you're running {data.currentVersion}.
          {' '}
          {data.releaseUrl && (
            <Anchor href={data.releaseUrl} target="_blank" rel="noreferrer" size="sm">
              View changelog
            </Anchor>
          )}
          {data.isDocker
            ? ' Pull the new image and recreate the container to update.'
            : ' Pull the latest code and rebuild to update.'}
        </Text>
        <ActionIcon
          variant="subtle"
          color="gray"
          size="sm"
          aria-label="Dismiss"
          onClick={() => setDismissedVersion(data.latestVersion)}
        >
          <IconX size={16} />
        </ActionIcon>
      </Group>
    </Alert>
  )
}
