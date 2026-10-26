import { useState, type ReactNode } from 'react'
import { Button, Group, Modal, Stack, Text } from '@mantine/core'
import { notifications } from '@mantine/notifications'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { t as now } from '@lingui/core/macro'
import {
  usePausePageConversion,
  useSavePageConversion,
  useStartPageConversion,
  type PageConversionSettings,
  type PageConversionStatus,
} from '../../api/pageConversion'
import { FORMAT_LABELS, STOP_HINTS, STOP_LABELS } from './labels'

type Action = 'save' | 'start'

/** Last step: what will be saved, and the only place anything is written or started. */
export function StartStep({
  value,
  status,
  onDone,
}: {
  value: PageConversionSettings
  status: PageConversionStatus
  onDone: () => void
}) {
  const save = useSavePageConversion()
  const start = useStartPageConversion()
  const pause = usePausePageConversion()
  const [asking, setAsking] = useState<Action | null>(null)

  const saved = status.settings
  const changed =
    value.format !== saved.format || value.stop !== saved.stop || value.pngLossless !== saved.pngLossless
  const running = status.pass.state === 'running'
  const busy = save.isPending || start.isPending || pause.isPending

  const run = async (action: Action, withPass: 'leave' | 'pause' | 'restart') => {
    try {
      if (running && withPass !== 'leave') await pause.mutateAsync(undefined)
      await save.mutateAsync(value)
      if (value.format && (withPass === 'restart' || (action === 'start' && !running))) {
        await start.mutateAsync(undefined)
      }
      notifications.show({ message: now`Saved`, color: 'var(--ok)' })
      setAsking(null)
      onDone()
    } catch {
      setAsking(null)
    }
  }

  const request = (action: Action) => {
    if (running && changed) setAsking(action)
    else void run(action, 'leave')
  }

  return (
    <Stack gap="lg">
      <Summary value={value} />

      <Text size="sm" c="var(--ink-3)">
        {value.format ? (
          <Plural
            value={status.library.pendingFiles}
            one="Nothing is saved until you press a button below. The pass will check # file that has not been looked at yet. Files converted earlier are not converted again."
            other="Nothing is saved until you press a button below. The pass will check # files that have not been looked at yet. Files converted earlier are not converted again."
          />
        ) : (
          <Trans>Nothing is saved until you press the button below. Files converted earlier stay as they are.</Trans>
        )}
      </Text>

      <Group gap="sm">
        {value.format && (
          <Button loading={busy && asking === null} disabled={busy} onClick={() => request('start')}>
            <Trans>Save and start</Trans>
          </Button>
        )}
        <Button variant="default" disabled={busy} onClick={() => request('save')}>
          <Trans>Save only</Trans>
        </Button>
      </Group>

      <Modal
        opened={asking !== null}
        onClose={() => setAsking(null)}
        title={<Trans>Pause the running pass?</Trans>}
        centered
      >
        <Stack gap="sm">
          <Text size="sm">
            <Trans>Pause the running pass and restart it with the new settings?</Trans>
          </Text>
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setAsking(null)}>
              <Trans>Cancel</Trans>
            </Button>
            <Button variant="default" loading={busy} onClick={() => asking && void run(asking, 'pause')}>
              <Trans>Save and pause</Trans>
            </Button>
            <Button loading={busy} onClick={() => asking && void run(asking, 'restart')}>
              <Trans>Restart</Trans>
            </Button>
          </Group>
        </Stack>
      </Modal>
    </Stack>
  )
}

function Summary({ value }: { value: PageConversionSettings }) {
  const { t } = useLingui()
  const stop = t(STOP_LABELS[value.stop])
  const hint = t(STOP_HINTS[value.stop])
  const quality = t`${stop}. ${hint}`
  return (
    <Stack gap="xs" component="dl" m={0}>
      <Row label={<Trans>Format</Trans>} value={value.format ? t(FORMAT_LABELS[value.format]) : t`Off`} />
      {value.format && (
        <>
          <Row label={<Trans>Quality</Trans>} value={quality} />
          <Row
            label={<Trans>PNG pages</Trans>}
            value={value.pngLossless || value.stop === 'lossless' ? t`Kept lossless` : t`Follow the quality`}
          />
        </>
      )}
    </Stack>
  )
}

function Row({ label, value }: { label: ReactNode; value: string }) {
  return (
    <Group gap="md" wrap="nowrap" align="baseline">
      <Text component="dt" size="sm" c="var(--ink-3)" w={110} style={{ flexShrink: 0 }}>
        {label}
      </Text>
      <Text component="dd" size="sm" m={0}>
        {value}
      </Text>
    </Group>
  )
}
