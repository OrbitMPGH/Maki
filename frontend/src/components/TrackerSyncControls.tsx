import { useState } from 'react'
import { Button, Group, Stack, Switch, Text } from '@mantine/core'
import { IconDownload } from '@tabler/icons-react'
import { RatingImportModal } from './RatingImportModal'
import { useScrobblePreferences, type ScrobbleConnection } from '../api/hooks'

/**
 * Per-tracker sync toggles ("scrobble reading" / "sync ratings") plus the rating-import action,
 * shown under each site block in Settings. Toggles save immediately; import needs a live
 * connection. `connection` is undefined until the scrobble status loads.
 */
export function TrackerSyncControls({
  service,
  label,
  connection,
}: {
  service: string
  label: string
  connection: ScrobbleConnection | undefined
}) {
  const prefs = useScrobblePreferences()
  const [importOpen, setImportOpen] = useState(false)

  const reading = connection?.syncReading ?? true
  const ratings = connection?.syncRatings ?? true
  const connected = connection?.connected ?? false

  const setPref = (patch: { reading?: boolean; ratings?: boolean }) =>
    prefs.mutate({
      service,
      reading: patch.reading ?? reading,
      ratings: patch.ratings ?? ratings,
    })

  return (
    <Stack gap={6} mt={4}>
      <Group gap="lg">
        <Switch
          size="xs"
          label="Scrobble reading"
          checked={reading}
          disabled={prefs.isPending || !connection}
          onChange={(e) => setPref({ reading: e.currentTarget.checked })}
        />
        <Switch
          size="xs"
          label="Sync ratings"
          checked={ratings}
          disabled={prefs.isPending || !connection}
          onChange={(e) => setPref({ ratings: e.currentTarget.checked })}
        />
      </Group>
      <Group gap="xs">
        <Button
          size="compact-xs"
          variant="light"
          leftSection={<IconDownload size={13} />}
          disabled={!connected}
          onClick={() => setImportOpen(true)}
        >
          Import ratings
        </Button>
        {!connected && (
          <Text size="xs" c="dimmed">
            Connect on the Scrobble page to import.
          </Text>
        )}
      </Group>
      <RatingImportModal
        service={service}
        label={label}
        opened={importOpen}
        onClose={() => setImportOpen(false)}
      />
    </Stack>
  )
}
