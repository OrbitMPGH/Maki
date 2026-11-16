import { useState } from 'react'
import { Button, Group, Modal, Stack, Text } from '@mantine/core'
import { notifications } from '../../lib/toast'
import { Trans } from '@lingui/react/macro'
import { t as now } from '@lingui/core/macro'
import {
  usePausePageConversion,
  useSavePageConversionPlan,
  useStartPageConversion,
  type PageConversionRule,
  type PageConversionSettings,
  type PageConversionStatus,
} from '../../api/pageConversion'
import { sameRules, toRuleDtos } from './ruleTemplates'

type Action = 'save' | 'start'

/** Whether the plan on screen differs from the one saved on the server. */
export function planChanged(
  value: PageConversionSettings,
  saved: PageConversionSettings,
  rules: readonly PageConversionRule[],
  savedRules: readonly PageConversionRule[],
): boolean {
  return (
    value.format !== saved.format ||
    value.stop !== saved.stop ||
    value.pngLossless !== saved.pngLossless ||
    (value.splitHeight ?? null) !== (saved.splitHeight ?? null) ||
    (value.colourFormat ?? null) !== (saved.colourFormat ?? null) ||
    (value.colourStop ?? null) !== (saved.colourStop ?? null) ||
    !sameRules(rules, savedRules)
  )
}

/** The only place anything is written or started. Asks first when a pass is running under the old plan. */
export function SaveActions({
  value,
  rules,
  changed,
  status,
  onDone,
}: {
  value: PageConversionSettings
  rules: PageConversionRule[]
  changed: boolean
  status: PageConversionStatus
  onDone: () => void
}) {
  const save = useSavePageConversionPlan()
  const start = useStartPageConversion()
  const pause = usePausePageConversion()
  const [asking, setAsking] = useState<Action | null>(null)

  const running = status.pass.state === 'running'
  const busy = save.isPending || start.isPending || pause.isPending

  const run = async (action: Action, withPass: 'leave' | 'pause' | 'restart') => {
    try {
      if (running && withPass !== 'leave') await pause.mutateAsync(undefined)
      await save.mutateAsync({ settings: value, rules: toRuleDtos(rules) })
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
    <>
      <Group gap="sm" wrap="nowrap">
        <Button variant="default" disabled={busy} onClick={() => request('save')}>
          <Trans>Save only</Trans>
        </Button>
        {value.format && (
          <Button loading={busy && asking === null} disabled={busy} onClick={() => request('start')}>
            <Trans>Save and start</Trans>
          </Button>
        )}
      </Group>

      <Modal opened={asking !== null} onClose={() => setAsking(null)} title={<Trans>Pause the running pass?</Trans>} centered>
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
    </>
  )
}
