import { Button, Group, Modal, Stack, Text } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { useSetSoFarSync } from '../api/hooks'
import { closeSoFarSyncAsk, useSoFarSyncAsk } from '../lib/soFarSyncAsk'

/**
 * The one-time question after a reader's first "so far" score with a rating-syncing tracker signed in:
 * should those scores go to the tracker as well? Either answer is stored per tracker, which is what
 * stops the question coming back; closing the dialog stores nothing.
 */
export function SoFarSyncAskDialog() {
  const ask = useSoFarSyncAsk()
  const save = useSetSoFarSync()
  const { t, i18n } = useLingui()

  const trackers = ask ? new Intl.ListFormat(i18n.locale, { type: 'conjunction' }).format(ask.trackers.map((x) => x.label)) : ''
  const answer = (enabled: boolean) => {
    if (!ask) return
    save.mutate(
      { services: ask.trackers.map((x) => x.service), enabled, seriesId: ask.seriesId },
      { onSettled: () => closeSoFarSyncAsk(true) },
    )
  }

  return (
    <Modal
      opened={ask != null}
      onClose={() => closeSoFarSyncAsk(false)}
      title={t`Send "so far" scores to ${trackers} too?`}
      centered
    >
      <Stack gap="sm">
        <Text size="sm">
          <Trans>
            Right now a "so far" score stays in Maki, and only your final rating goes to your trackers. You can
            change this for each tracker in Settings.
          </Trans>
        </Text>
        <Group justify="flex-end">
          <Button variant="default" disabled={save.isPending} onClick={() => answer(false)}>
            <Trans>Keep them in Maki</Trans>
          </Button>
          <Button loading={save.isPending} onClick={() => answer(true)}>
            <Trans>Send them</Trans>
          </Button>
        </Group>
      </Stack>
    </Modal>
  )
}
