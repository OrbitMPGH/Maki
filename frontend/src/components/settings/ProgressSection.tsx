import { useEffect, useState } from 'react'
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

const PERIODS: { value: ReadingGoal['period']; label: string }[] = [
  { value: 'Day', label: 'Every day' },
  { value: 'Week', label: 'Every week' },
  { value: 'Month', label: 'Every month' },
  { value: 'Year', label: 'Every year' },
]

const METRICS: { value: ReadingGoal['metric']; label: string }[] = [
  { value: 'Chapters', label: 'chapters' },
  { value: 'Minutes', label: 'minutes read' },
  { value: 'SeriesFinished', label: 'series finished' },
]

/** What the browser thinks the user's zone is, used to prefill and as the "detect" value. */
function browserTimeZone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone ?? ''
  } catch {
    return ''
  }
}

export function ProgressSection() {
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

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4}>Progress & achievements</Title>
      <Text size="sm" c="dimmed" mb="md">
        Levels, badges and streaks worked out from your reading history. All of it is derived, so
        switching this off stores nothing and switching it back on brings everything back.
      </Text>

      <Stack gap="md">
        <Switch
          checked={settings.enabled}
          onChange={(e) => patch({ enabled: e.currentTarget.checked })}
          label="Track progress and achievements"
          description="Off hides the Home section, the all-time tab on Stats, and unlock notifications."
          aria-label="Track progress and achievements"
        />

        <Switch
          checked={settings.showStreaks}
          onChange={(e) => patch({ showStreaks: e.currentTarget.checked })}
          disabled={!settings.enabled}
          label="Show reading streaks"
          description="One missed day a week is forgiven, and today never breaks a streak."
          aria-label="Show reading streaks"
        />

        <Switch
          checked={settings.showOnLeaderboard}
          onChange={(e) => patch({ showOnLeaderboard: e.currentTarget.checked })}
          disabled={!settings.enabled}
          label="Compare with other users on this instance"
          description="Shows your name, level, chapters read and streak to everyone who also opted in. Never anything about which series you read."
          aria-label="Compare with other users on this instance"
        />

        <Select
          label="Time zone"
          description="Decides when your reading day ends, which is what streaks and daily goals count against."
          data={[
            { value: '', label: 'UTC' },
            ...(browserTimeZone() ? [{ value: browserTimeZone(), label: `${browserTimeZone()} (this browser)` }] : []),
            ...(settings.timeZone && settings.timeZone !== browserTimeZone()
              ? [{ value: settings.timeZone, label: settings.timeZone }]
              : []),
          ]}
          value={settings.timeZone}
          onChange={(v) => patch({ timeZone: v ?? '' })}
          disabled={!settings.enabled}
        />

        <Stack gap="xs">
          <Text fw={500} size="sm">
            Reading goals
          </Text>
          <Text size="xs" c="dimmed">
            Optional, and yours to set. Maki never adds one for you.
          </Text>

          {(summary?.goals ?? []).map((goal) => (
            <Group key={goal.id} justify="space-between" wrap="nowrap">
              <Text size="sm">
                {PERIODS.find((p) => p.value === goal.period)?.label}: {goal.target}{' '}
                {METRICS.find((m) => m.value === goal.metric)?.label}
              </Text>
              <ActionIcon
                variant="subtle"
                color="red"
                onClick={() => deleteGoal.mutate(goal.id)}
                aria-label="Remove goal"
              >
                <IconTrash size={16} />
              </ActionIcon>
            </Group>
          ))}

          <Group gap="xs" align="flex-end" wrap="wrap">
            <Select
              data={PERIODS}
              value={period}
              onChange={(v) => v && setPeriod(v as ReadingGoal['period'])}
              w={140}
              aria-label="Goal period"
              disabled={!settings.enabled}
            />
            <NumberInput
              value={target}
              onChange={setTarget}
              min={1}
              w={100}
              aria-label="Goal target"
              disabled={!settings.enabled}
            />
            <Select
              data={METRICS}
              value={metric}
              onChange={(v) => v && setMetric(v as ReadingGoal['metric'])}
              w={160}
              aria-label="Goal metric"
              disabled={!settings.enabled}
            />
            <Button
              variant="light"
              disabled={!settings.enabled || Number(target) < 1}
              onClick={() => saveGoal.mutate({ period, metric, target: Number(target) })}
            >
              Set goal
            </Button>
          </Group>
        </Stack>
      </Stack>
    </Card>
  )
}
