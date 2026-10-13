import { useMemo, useState } from 'react'
import { Alert, Button, Group, Modal, Select, Stack, Text, TextInput } from '@mantine/core'
import { IconPlayerPause, IconPlayerPlay } from '@tabler/icons-react'
import { notifications } from '@mantine/notifications'
import { Trans, useLingui } from '@lingui/react/macro'
import { usePauseQueue, useQueuePause, useResumeQueue, useSources } from '../api/hooks'
import { formatDateTime, formatTime } from '../format'
import { useSourceLabel } from '../sourceLabels'

const ALL_SOURCES = '*'

type ResumeChoice = 'manual' | 'hour' | 'tomorrow' | 'custom'

const TOMORROW_MORNING_HOUR = 8

/** "14:00" for a time later today, the date and time for anything further out. */
function untilText(until: string): string {
  const date = new Date(until)
  return date.toDateString() === new Date().toDateString() ? formatTime(date) : formatDateTime(date)
}

function toLocalInput(date: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`
}

function resumeTime(choice: ResumeChoice, custom: string): Date | null {
  const now = new Date()
  if (choice === 'hour') return new Date(now.getTime() + 60 * 60 * 1000)
  if (choice === 'tomorrow') {
    const morning = new Date(now)
    morning.setDate(morning.getDate() + 1)
    morning.setHours(TOMORROW_MORNING_HOUR, 0, 0, 0)
    return morning
  }
  if (choice === 'custom') {
    const picked = new Date(custom)
    return Number.isNaN(picked.getTime()) ? null : picked
  }
  return null
}

/** Opens the dialog for pausing every scraper download, or one source's. */
export function QueuePauseButton() {
  const { t } = useLingui()
  const sourceLabel = useSourceLabel()
  const sources = useSources().data
  const pause = usePauseQueue()

  const [open, setOpen] = useState(false)
  const [scope, setScope] = useState(ALL_SOURCES)
  const [choice, setChoice] = useState<ResumeChoice>('manual')
  const [custom, setCustom] = useState('')

  const scopeOptions = useMemo(
    () => [
      { value: ALL_SOURCES, label: t`All sources` },
      ...(sources ?? [])
        .map((s) => ({ value: s.name, label: sourceLabel(s.name) }))
        .sort((a, b) => a.label.localeCompare(b.label)),
    ],
    [sources, sourceLabel, t],
  )

  const resumeOptions = [
    { value: 'manual', label: t`Until I resume` },
    { value: 'hour', label: t`For 1 hour` },
    { value: 'tomorrow', label: t`Until tomorrow morning` },
    { value: 'custom', label: t`Until a time I choose` },
  ]

  const resumeAt = resumeTime(choice, custom)
  const invalid = choice === 'custom' && (resumeAt === null || resumeAt.getTime() <= Date.now())

  const openDialog = () => {
    setCustom(toLocalInput(new Date(Date.now() + 60 * 60 * 1000)))
    setOpen(true)
  }

  const submit = () =>
    pause.mutate(
      {
        source: scope === ALL_SOURCES ? undefined : scope,
        resumeAt: resumeAt?.toISOString(),
      },
      {
        onSuccess: () => setOpen(false),
        onError: () => notifications.show({ message: t`Couldn't pause the downloads`, color: 'var(--danger)' }),
      },
    )

  return (
    <>
      <Button variant="light" leftSection={<IconPlayerPause size={16} />} onClick={openDialog}>
        <Trans>Pause</Trans>
      </Button>

      <Modal opened={open} onClose={() => setOpen(false)} centered title={<Trans>Pause downloads</Trans>}>
        <Stack gap="sm">
          <Text size="sm">
            <Trans>
              Maki stops starting new downloads. Chapters already downloading finish, and nothing is removed from the
              queue. Torrents already handed to qBittorrent keep downloading.
            </Trans>
          </Text>
          <Select
            label={t`What to pause`}
            data={scopeOptions}
            value={scope}
            onChange={(value) => setScope(value ?? ALL_SOURCES)}
            allowDeselect={false}
            searchable
            comboboxProps={{ withinPortal: true }}
          />
          <Select
            label={t`Resume`}
            data={resumeOptions}
            value={choice}
            onChange={(value) => setChoice((value as ResumeChoice | null) ?? 'manual')}
            allowDeselect={false}
            comboboxProps={{ withinPortal: true }}
          />
          {choice === 'custom' && (
            <TextInput
              type="datetime-local"
              label={t`Resume at`}
              value={custom}
              onChange={(e) => setCustom(e.currentTarget.value)}
              error={invalid ? t`Pick a time in the future` : undefined}
            />
          )}
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setOpen(false)}>
              <Trans>Cancel</Trans>
            </Button>
            <Button loading={pause.isPending} disabled={invalid} onClick={submit}>
              <Trans>Pause</Trans>
            </Button>
          </Group>
        </Stack>
      </Modal>
    </>
  )
}

/** One line per active pause, each with its own Resume. Renders nothing while the queue is running. */
export function QueuePauseBanner({ canManage }: { canManage: boolean }) {
  const { t } = useLingui()
  const sourceLabel = useSourceLabel()
  const pause = useQueuePause().data
  const resume = useResumeQueue()

  if (!pause || (pause.all === null && pause.sources.length === 0)) return null

  const lines: { key: string; source: string | undefined; text: string }[] = []
  if (pause.all) {
    const time = pause.all.until ? untilText(pause.all.until) : null
    lines.push({
      key: ALL_SOURCES,
      source: undefined,
      text: time ? t`Downloads paused until ${time}` : t`Downloads paused`,
    })
  }
  for (const s of pause.sources) {
    const name = sourceLabel(s.sourceName)
    const time = s.until ? untilText(s.until) : null
    lines.push({
      key: s.sourceName,
      source: s.sourceName,
      text: time ? t`${name} paused until ${time}` : t`${name} paused`,
    })
  }

  return (
    <Alert color="var(--warn)" variant="light" icon={<IconPlayerPause size={16} />} mb="md">
      <Stack gap="xs">
        {lines.map((line) => (
          <Group key={line.key} justify="space-between" wrap="nowrap">
            <Text size="sm" fw={600}>
              {line.text}
            </Text>
            {canManage && (
              <Button
                size="compact-sm"
                variant="light"
                leftSection={<IconPlayerPlay size={14} />}
                loading={resume.isPending && resume.variables === line.source}
                onClick={() =>
                  resume.mutate(line.source, {
                    onError: () => notifications.show({ message: t`Couldn't resume the downloads`, color: 'var(--danger)' }),
                  })
                }
              >
                <Trans>Resume</Trans>
              </Button>
            )}
          </Group>
        ))}
        <Text size="xs" c="var(--ink-3)">
          <Trans>
            Chapters already downloading finish. Torrents already handed to qBittorrent keep downloading.
          </Trans>
        </Text>
      </Stack>
    </Alert>
  )
}
