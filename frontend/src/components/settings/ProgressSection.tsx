import { useEffect, useMemo, useState } from 'react'
import {
  ActionIcon,
  Button,
  Card,
  Group,
  NumberInput,
  Select,
  Stack,
  Switch,
  Text,
  Title,
} from '@mantine/core'
import { IconTrash } from '@tabler/icons-react'
import {
  useDeleteReadingGoal,
  useProgressSettings,
  useProgressSummary,
  useSaveProgressSettings,
  useSaveReadingGoal,
} from '../../api/hooks'
import type { ProgressSettings, ReadingGoal } from '../../api/hooks'
import { useLingui } from '@lingui/react'
import { Trans, useLingui as useLinguiMacro } from '@lingui/react/macro'
import { msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'

/**
 * Descriptors, not strings: this table is built once when the module loads, so a rendered string
 * here would be stuck in whichever language was active at that moment. `usePeriodOptions` and
 * `useMetricOptions` render them at the call site.
 */
const PERIOD_DEFS: { value: ReadingGoal['period']; label: MessageDescriptor }[] = [
  { value: 'Day', label: msg`Every day` },
  { value: 'Week', label: msg`Every week` },
  { value: 'Month', label: msg`Every month` },
  { value: 'Year', label: msg`Every year` },
]

const METRIC_DEFS: { value: ReadingGoal['metric']; label: MessageDescriptor }[] = [
  { value: 'Chapters', label: msg`chapters` },
  { value: 'Minutes', label: msg`minutes read` },
  { value: 'SeriesFinished', label: msg`series finished` },
]

function usePeriodOptions() {
  const { _, i18n } = useLingui()
  return useMemo(
    () => PERIOD_DEFS.map((p) => ({ value: p.value, label: _(p.label) })),
    [_, i18n.locale],
  )
}

function useMetricOptions() {
  const { _, i18n } = useLingui()
  return useMemo(
    () => METRIC_DEFS.map((m) => ({ value: m.value, label: _(m.label) })),
    [_, i18n.locale],
  )
}

/** What the browser thinks the user's zone is, used to prefill and as the "detect" value. */
function browserTimeZone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone ?? ''
  } catch {
    return ''
  }
}

export function ProgressSection() {
  const { t } = useLinguiMacro()
  const periods = usePeriodOptions()
  const metrics = useMetricOptions()
  const { data: settings } = useProgressSettings()
  const { data: summary } = useProgressSummary()
  const save = useSaveProgressSettings()
  const saveGoal = useSaveReadingGoal()
  const deleteGoal = useDeleteReadingGoal()

  const [period, setPeriod] = useState<ReadingGoal['period']>('Day')
  const [metric, setMetric] = useState<ReadingGoal['metric']>('Chapters')
  const [target, setTarget] = useState<number | string>(3)

  // Seed the time zone from the browser the first time somebody opens this, so streaks land on the
  // right day without anybody having to think about it. Only when it is genuinely unset — never
  // overwrite a zone the user chose.
  useEffect(() => {
    if (settings && settings.timeZone === '' && browserTimeZone()) {
      save.mutate({ ...settings, timeZone: browserTimeZone() })
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [settings?.timeZone])

  if (!settings) {
    return null
  }

  const patch = (changes: Partial<ProgressSettings>) => save.mutate({ ...settings, ...changes })
  const zone = browserTimeZone()

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4}>
        <Trans>Progress & achievements</Trans>
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        <Trans>
          Levels, badges and streaks worked out from your reading history. All of it is derived, so
          switching this off stores nothing and switching it back on brings everything back.
        </Trans>
      </Text>

      <Stack gap="md">
        <Switch
          checked={settings.enabled}
          onChange={(e) => patch({ enabled: e.currentTarget.checked })}
          label={t`Track progress and achievements`}
          description={t`Off hides the Home section, the all-time tab on Stats, and unlock notifications.`}
          aria-label={t`Track progress and achievements`}
        />

        <Switch
          checked={settings.showStreaks}
          onChange={(e) => patch({ showStreaks: e.currentTarget.checked })}
          disabled={!settings.enabled}
          label={t`Show reading streaks`}
          description={t`One missed day a week is forgiven, and today never breaks a streak.`}
          aria-label={t`Show reading streaks`}
        />

        <Switch
          checked={settings.showOnLeaderboard}
          onChange={(e) => patch({ showOnLeaderboard: e.currentTarget.checked })}
          disabled={!settings.enabled}
          label={t`Compare with other users on this instance`}
          description={t`Shows your name, level, chapters read and streak to everyone who also opted in. Never anything about which series you read.`}
          aria-label={t`Compare with other users on this instance`}
        />

        <Select
          label={t`Time zone`}
          description={t`Decides when your reading day ends, which is what streaks and daily goals count against.`}
          data={[
            { value: '', label: t`UTC` },
            ...(zone ? [{ value: zone, label: t`${zone} (this browser)` }] : []),
            ...(settings.timeZone && settings.timeZone !== zone
              ? [{ value: settings.timeZone, label: settings.timeZone }]
              : []),
          ]}
          value={settings.timeZone}
          onChange={(v) => patch({ timeZone: v ?? '' })}
          disabled={!settings.enabled}
        />

        <Stack gap="xs">
          <Text fw={500} size="sm">
            <Trans>Reading goals</Trans>
          </Text>
          <Text size="xs" c="dimmed">
            <Trans>Optional, and yours to set. Maki never adds one for you.</Trans>
          </Text>

          {(summary?.goals ?? []).map((goal) => (
            <Group key={goal.id} justify="space-between" wrap="nowrap">
              <Text size="sm">
                {periods.find((p) => p.value === goal.period)?.label}: {goal.target}{' '}
                {metrics.find((m) => m.value === goal.metric)?.label}
              </Text>
              <ActionIcon
                variant="subtle"
                color="red"
                onClick={() => deleteGoal.mutate(goal.id)}
                aria-label={t`Remove goal`}
              >
                <IconTrash size={16} />
              </ActionIcon>
            </Group>
          ))}

          <Group gap="xs" align="flex-end" wrap="wrap">
            <Select
              data={periods}
              value={period}
              onChange={(v) => v && setPeriod(v as ReadingGoal['period'])}
              w={140}
              aria-label={t`Goal period`}
              disabled={!settings.enabled}
            />
            <NumberInput
              value={target}
              onChange={setTarget}
              min={1}
              w={100}
              aria-label={t`Goal target`}
              disabled={!settings.enabled}
            />
            <Select
              data={metrics}
              value={metric}
              onChange={(v) => v && setMetric(v as ReadingGoal['metric'])}
              w={160}
              aria-label={t`Goal metric`}
              disabled={!settings.enabled}
            />
            <Button
              variant="light"
              disabled={!settings.enabled || Number(target) < 1}
              onClick={() => saveGoal.mutate({ period, metric, target: Number(target) })}
            >
              <Trans>Set goal</Trans>
            </Button>
          </Group>
        </Stack>
      </Stack>
    </Card>
  )
}
