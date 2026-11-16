import { NumberInput, Stack, Switch, Text } from '@mantine/core'
import { useState } from 'react'
import { Trans, useLingui } from '@lingui/react/macro'
import { t as now } from '@lingui/core/macro'
import { useReadingStatusSettings, useSaveReadingStatusSettings, type ReadingStatusSettings } from '../../api/hooks'
import { notifications } from '../../lib/toast'
import { SettingsSection } from '../../pages/settings/SettingsSection'

const DEFAULT_HOLD_DAYS = 30
const DEFAULT_DROP_DAYS = 90

/** The two optional rules that move a series to On hold or Dropped after a stretch without reading. */
export function ReadingStatusSection() {
  const { t } = useLingui()
  const { data } = useReadingStatusSettings()
  const save = useSaveReadingStatusSettings()
  const [draft, setDraft] = useState<Partial<Record<keyof ReadingStatusSettings, number | ''>>>({})

  const current: ReadingStatusSettings = data ?? { autoHoldDays: null, autoDropDays: null }
  const update = (next: Partial<ReadingStatusSettings>) =>
    save.mutate(
      { ...current, ...next },
      { onSuccess: () => notifications.show({ message: now`Saved`, color: 'var(--ok)' }) },
    )

  const rule = (
    field: keyof ReadingStatusSettings,
    fallback: number,
    switchLabel: string,
    help: React.ReactNode,
    daysLabel: string,
  ) => {
    const days = current[field]
    const shown = draft[field] ?? days ?? fallback
    const commit = () => {
      const value = draft[field]
      setDraft((d) => ({ ...d, [field]: undefined }))
      if (days == null || value === undefined) return
      const clamped = value === '' ? fallback : Math.min(3650, Math.max(1, Math.round(value)))
      if (clamped !== days) update({ [field]: clamped })
    }
    return (
      <div>
        <Switch
          label={switchLabel}
          checked={days != null}
          disabled={!data}
          onChange={(e) => update({ [field]: e.currentTarget.checked ? fallback : null })}
        />
        <Text size="xs" c="var(--ink-3)" mt={4}>
          {help}
        </Text>
        {days != null && (
          <NumberInput
            mt="xs"
            w={180}
            min={1}
            max={3650}
            allowDecimal={false}
            suffix={` ${t`days`}`}
            aria-label={daysLabel}
            value={shown}
            onChange={(v) => setDraft((d) => ({ ...d, [field]: typeof v === 'number' ? v : '' }))}
            onBlur={commit}
          />
        )}
      </div>
    )
  }

  return (
    <SettingsSection id="reading-status" title={<Trans>Reading status</Trans>}>
      <Stack gap="md">
        {rule(
          'autoHoldDays',
          DEFAULT_HOLD_DAYS,
          t`Put a series on hold when I stop reading it`,
          <Trans>
            Only for series with unread chapters downloaded. A series you are caught up on is left alone.
          </Trans>,
          t`Days without reading before a series is put on hold`,
        )}
        {rule(
          'autoDropDays',
          DEFAULT_DROP_DAYS,
          t`Drop a series when I stop reading it`,
          <Trans>
            Applies to series you are reading and ones the on-hold rule paused. A series you put on hold yourself is never dropped.
          </Trans>,
          t`Days without reading before a series is dropped`,
        )}
      </Stack>
    </SettingsSection>
  )
}
