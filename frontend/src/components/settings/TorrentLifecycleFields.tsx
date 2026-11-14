import { useState } from 'react'
import { SettingsGroupTitle } from './SettingsGroupTitle'
import { Box, Button, Group, NumberInput, SegmentedControl, Stack, Switch, Text } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { notifications } from '../../lib/toast'
import { savedToast } from '../../pages/settings/sharedRecord'
import { SettingsHelp } from './SettingsHelp'
import { SettingsNumberInput } from './SettingsNumberInput'
import { DeclinedReleasesModal } from './DeclinedReleasesModal'
import { targetOf, type TorrentLifecycleForm } from './useTorrentLifecycleForm'
import { NewSettingPill } from './NewSettingPill'
import { useNewControlMark } from '../../pages/settings/newSettings'
import {
  useConnectionSettings,
  useDownloadClientKinds,
  useDownloadSettings,
  useSaveDownloadSettings,
  type TorrentRemovalMode,
} from '../../api/hooks'
import {
  MAX_SEED_MINUTES,
  MAX_SEED_RATIO,
  MAX_STALL_HOURS,
  useImportSeedLimits,
} from '../../api/torrentLifecycle'

/** The name of the configured client, for "read from qBittorrent". */
function useClientName() {
  const { data: client } = useConnectionSettings<{ kind: string | null }>('downloadclient')
  const { data: kinds } = useDownloadClientKinds()
  const kind = client?.kind ?? 'qbittorrent'
  return { kind, name: kinds?.find((k) => k.kind === kind)?.name ?? kind }
}

/** A short elbow joining a nested setting to the switch it depends on. */
function Elbow() {
  return (
    <Box
      aria-hidden
      w={10}
      h={22}
      ml={18}
      style={{
        flexShrink: 0,
        borderInlineStart: '2px solid var(--border-strong)',
        borderBottom: '2px solid var(--border-strong)',
        borderEndStartRadius: 4,
      }}
    />
  )
}

function SeedTargetFields({ form, clientName, clientKind }: { form: TorrentLifecycleForm; clientName: string; clientKind: string }) {
  const { t } = useLingui()
  const importLimits = useImportSeedLimits()
  const noTargets = targetOf(form.seedRatio) === null && targetOf(form.seedMinutes) === null

  return (
    <Stack gap={6} ml={36} mt="sm">
      <SettingsHelp>
        <Trans>Maki removes a torrent once it reaches either target, whichever comes first. Leave a field empty for no target.</Trans>
      </SettingsHelp>
      <Group align="flex-end" wrap="wrap" gap="sm">
        <NumberInput
          label={t`Seed ratio`}
          placeholder={t`No target`}
          min={0}
          max={MAX_SEED_RATIO}
          decimalScale={2}
          clampBehavior="blur"
          value={form.seedRatio}
          onChange={form.setSeedRatio}
          disabled={!form.loaded}
          w={150}
        />
        <NumberInput
          label={t`Seeding time (minutes)`}
          description={clientKind === 'qbittorrent' ? undefined : t`${clientName} has no seeding time limit to read.`}
          placeholder={t`No target`}
          min={0}
          max={MAX_SEED_MINUTES}
          allowDecimal={false}
          clampBehavior="blur"
          value={form.seedMinutes}
          onChange={form.setSeedMinutes}
          disabled={!form.loaded}
          w={200}
        />
        <Button
          variant="default"
          loading={importLimits.isPending}
          onClick={() =>
            importLimits.mutate(undefined, {
              onSuccess: (imported) => {
                form.setSeedRatio(imported.seedRatio ?? '')
                form.setSeedMinutes(imported.seedMinutes ?? '')
                notifications.show({
                  message: imported.found
                    ? t`Read the seed limits from ${clientName}`
                    : t`${clientName} has no seed limits set, so there are no targets`,
                  color: imported.found ? 'var(--ok)' : undefined,
                })
              },
            })
          }
        >
          <Trans>Read from {clientName}</Trans>
        </Button>
      </Group>
      {noTargets && form.loaded && (
        <Text size="sm" c="var(--warn)">
          <Trans>With no target, torrents stay in the client after import.</Trans>
        </Text>
      )}
    </Stack>
  )
}

/**
 * The finished and stalled torrent settings inside the download client card. The switches save on change
 * with the download settings record; the seed targets and the stall limit save with the card.
 */
export function TorrentLifecycleFields({ form }: { form: TorrentLifecycleForm }) {
  const { t } = useLingui()
  const { data: settings } = useDownloadSettings()
  const save = useSaveDownloadSettings()
  const client = useClientName()
  const [declinedOpen, setDeclinedOpen] = useState(false)
  const removalMark = useNewControlMark('remove-after-seeding')
  const stalledMark = useNewControlMark('stalled-torrents')
  const [lastMode, setLastMode] = useState<TorrentRemovalMode>('afterimport')
  const pending = save.isPending ? save.variables : undefined
  const useHardlinks = pending?.useHardlinks ?? settings?.useHardlinks ?? true
  const mode: TorrentRemovalMode = pending?.removeCompleted ?? settings?.removeCompleted ?? 'never'
  const removing = mode !== 'never'
  const removeData = pending?.removeData ?? settings?.removeData ?? true

  const setMode = (next: TorrentRemovalMode) => {
    if (next !== 'never') setLastMode(next)
    save.mutate({ removeCompleted: next }, { onSuccess: savedToast })
  }

  return (
    <>
      <Stack gap={0} mt="lg">
        <SettingsGroupTitle mb={4}>
          <Trans>Finished torrents</Trans>
        </SettingsGroupTitle>
        <SettingsHelp mb="xs">
          <Trans>Needs the download folder and library on one filesystem, otherwise Maki copies.</Trans>
        </SettingsHelp>
        <Switch
          label={t`Hardlink imported torrents when possible`}
          checked={useHardlinks}
          disabled={!settings}
          onChange={(e) => {
            save.mutate({ useHardlinks: e.currentTarget.checked }, { onSuccess: savedToast })
          }}
        />
        <div id="setting-remove-after-seeding" {...removalMark}>
          <Switch
            mt="md"
            label={t`Remove torrents from the download client after import`}
            checked={removing}
            disabled={!settings}
            onChange={(e) => setMode(e.currentTarget.checked ? lastMode : 'never')}
          />
          <Group gap={8} wrap="nowrap" align="flex-start" mt={10}>
            <Elbow />
            <SegmentedControl
              size="xs"
              aria-label={t`When to remove`}
              disabled={!settings || !removing}
              value={removing ? mode : lastMode}
              onChange={(value) => setMode(value as TorrentRemovalMode)}
              data={[
                { value: 'afterimport', label: t`Right after import` },
                { value: 'afterseeding', label: t`After seeding` },
              ]}
            />
            <NewSettingPill id="remove-after-seeding" />
          </Group>
          {removing && mode === 'afterseeding' && (
            <SeedTargetFields form={form} clientName={client.name} clientKind={client.kind} />
          )}
          <Group gap={8} wrap="nowrap" align="flex-start">
            <Elbow />
            <Switch
              mt={10}
              label={t`Also delete the downloaded files`}
              checked={removeData}
              disabled={!settings || !removing}
              onChange={(e) => {
                save.mutate({ removeData: e.currentTarget.checked }, { onSuccess: savedToast })
              }}
            />
          </Group>
        </div>
      </Stack>

      <Stack id="setting-stalled-torrents" {...stalledMark} gap={0} mt="lg">
        <SettingsGroupTitle mb={4}>
          <Trans>Stalled torrents</Trans>
          <NewSettingPill id="stalled-torrents" />
        </SettingsGroupTitle>
        <SettingsHelp mb="xs">
          <Trans>
            A torrent that makes no progress, or has no seeds or peers connected, for this long is failed, taken out of
            the client and declined, and the automatic search looks for another release. 0 turns this off.
          </Trans>
        </SettingsHelp>
        <Group align="flex-end" wrap="wrap" gap="sm">
          <SettingsNumberInput
            label={t`Give up after (hours)`}
            min={0}
            max={MAX_STALL_HOURS}
            value={form.stallHours}
            onChange={form.setStallHours}
            disabled={!form.loaded}
          />
          <Button variant="default" onClick={() => setDeclinedOpen(true)}>
            <Trans>Declined releases</Trans>
          </Button>
        </Group>
      </Stack>
      <DeclinedReleasesModal opened={declinedOpen} onClose={() => setDeclinedOpen(false)} />
    </>
  )
}
