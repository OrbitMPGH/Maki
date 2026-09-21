import { useState } from 'react'
import { Button, Group, Stack, Switch, Text } from '@mantine/core'
import { IconDownload } from '@tabler/icons-react'
import { Trans, useLingui } from '@lingui/react/macro'
import { RatingImportModal } from './RatingImportModal'
import { useScrobblePreferences, type ScrobbleConnection } from '../api/hooks'

/**
 * Per-tracker sync toggles ("scrobble reading" / "sync ratings" / "use anime for taste") plus the
 * rating-import action, shown under each site block in Settings. Toggles save immediately; import
 * needs a live connection. `connection` is undefined until the scrobble status loads.
 *
 * The anime toggle only appears on trackers that can serve an anime list, which the server says.
 * It exists because a reader who scrobbles to two trackers has the same watch history on both, and
 * turning one off is how they stop the duplicate from reaching the recommender at all.
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
  const { t } = useLingui()

  const reading = connection?.syncReading ?? true
  const ratings = connection?.syncRatings ?? true
  const animeList = connection?.animeList ?? false
  const anime = connection?.animeSignals ?? true
  const connected = connection?.connected ?? false

  const setPref = (patch: { reading?: boolean; ratings?: boolean; anime?: boolean }) =>
    prefs.mutate({
      service,
      reading: patch.reading ?? reading,
      ratings: patch.ratings ?? ratings,
      // Only for trackers that have an anime list. Sending it for the rest would write a setting
      // nothing reads, and the request is the same shape for every block otherwise.
      anime: animeList ? patch.anime ?? anime : undefined,
    })

  return (
    <Stack gap={6} mt={4}>
      <Group gap="lg">
        <Switch
          size="xs"
          label={t`Scrobble reading`}
          checked={reading}
          disabled={prefs.isPending || !connection}
          onChange={(e) => setPref({ reading: e.currentTarget.checked })}
        />
        <Switch
          size="xs"
          label={t`Sync ratings`}
          checked={ratings}
          disabled={prefs.isPending || !connection}
          onChange={(e) => setPref({ ratings: e.currentTarget.checked })}
        />
        {animeList && (
          <Switch
            size="xs"
            label={t`Use anime for taste`}
            description={t`Watched anime from this tracker seeds recommendations`}
            checked={anime}
            disabled={prefs.isPending || !connection}
            onChange={(e) => setPref({ anime: e.currentTarget.checked })}
          />
        )}
      </Group>
      <Group gap="xs">
        <Button
          size="compact-xs"
          variant="light"
          leftSection={<IconDownload size={13} />}
          disabled={!connected}
          onClick={() => setImportOpen(true)}
        >
          <Trans>Import ratings</Trans>
        </Button>
        {!connected && (
          <Text size="xs" c="dimmed">
            <Trans>Connect on the Scrobble page to import.</Trans>
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
