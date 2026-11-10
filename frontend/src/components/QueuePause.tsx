import { useMemo, useState } from 'react'
import { Alert, Button, Group, Popover, Stack, Text, TextInput } from '@mantine/core'
import { IconClockPlay, IconPlayerPause, IconPlayerPlay, IconWorld } from '../lib/icons'
import { notifications } from '../lib/toast'
import { Trans, useLingui } from '@lingui/react/macro'
import { usePauseQueue, useQueuePause, useResumeQueue, useSources } from '../api/hooks'
import { formatDateTime, formatTime } from '../format'
import { useSourceLabel } from '../sourceLabels'
import { DrillPanes } from './ui/DrillPanes'
import { DrillBack, DrillChoices, DrillRow, useDrillPages } from './ui/DrillPanel'

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

type PausePage = 'main' | 'scope' | 'resume'

const PAUSE_PARENTS: Record<PausePage, PausePage> = { main: 'main', scope: 'main', resume: 'main' }

/** A drill panel for pausing every scraper download, or one source's. */
export function QueuePauseButton() {
  const { t } = useLingui()
  const sourceLabel = useSourceLabel()
  const sources = useSources().data
  const pause = usePauseQueue()
  const pages = useDrillPages<PausePage>('main', PAUSE_PARENTS)

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

  const resumeHint = (option: ResumeChoice): string => {
    if (option === 'manual') return t`Stays paused until you press Resume.`
    if (option === 'custom') return t`Pick the date and time below.`
    const at = resumeTime(option, '')
    const time = at ? untilText(at.toISOString()) : ''
    return t`Resumes at ${time}.`
  }
  const resumeOptions = [
    { value: 'manual', label: t`Until I resume` },
    { value: 'hour', label: t`For 1 hour` },
    { value: 'tomorrow', label: t`Until tomorrow morning` },
    { value: 'custom', label: t`Until a time I choose` },
  ] as const

  const resumeAt = resumeTime(choice, custom)
  const invalid = choice === 'custom' && (resumeAt === null || resumeAt.getTime() <= Date.now())

  const openPanel = () => {
    setCustom(toLocalInput(new Date(Date.now() + 60 * 60 * 1000)))
    pages.reset()
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
    <Popover
      opened={open}
      onChange={setOpen}
      position="bottom-end"
      width={320}
      shadow="md"
      trapFocus
      returnFocus
      closeOnEscape={false}
    >
      <Popover.Target>
        <Button
          variant="default"
          leftSection={<IconPlayerPause size={16} />}
          aria-expanded={open}
          onClick={() => (open ? setOpen(false) : openPanel())}
        >
          <Trans>Pause downloads</Trans>
        </Button>
      </Popover.Target>
      <Popover.Dropdown
        p={6}
        aria-label={t`Pause downloads`}
        onKeyDown={(event) => {
          if (event.key !== 'Escape') return
          event.stopPropagation()
          if (pages.page === 'main') setOpen(false)
          else pages.back()
        }}
      >
        <DrillPanes page={pages.page} depth={pages.depth} bleed={6}>
          {pages.page === 'main' && (
            <>
              <Text size="xs" c="var(--ink-3)" px={10} pt={4} pb={6}>
                <Trans>
                  Maki stops starting new downloads. Chapters already downloading finish, and nothing is removed from the
                  queue. Torrents already handed to the download client keep downloading.
                </Trans>
              </Text>
              <DrillRow
                ref={pages.rowRef('scope')}
                icon={IconWorld}
                label={<Trans>What to pause</Trans>}
                value={scopeOptions.find((o) => o.value === scope)?.label}
                onClick={() => pages.open('scope')}
              />
              <DrillRow
                ref={pages.rowRef('resume')}
                icon={IconClockPlay}
                label={<Trans>Resume</Trans>}
                value={resumeOptions.find((o) => o.value === choice)?.label}
                onClick={() => pages.open('resume')}
              />
              {choice === 'custom' && (
                <div className="drill-field">
                  <TextInput
                    type="datetime-local"
                    size="xs"
                    label={t`Resume at`}
                    value={custom}
                    onChange={(e) => setCustom(e.currentTarget.value)}
                    error={invalid ? t`Pick a time in the future` : undefined}
                  />
                </div>
              )}
            </>
          )}
          {pages.page === 'scope' && (
            <>
              <DrillBack ref={pages.backRef} title={<Trans>What to pause</Trans>} onBack={pages.back} />
              <div className="drill-scroll">
                <DrillChoices
                  label={t`What to pause`}
                  value={scope}
                  options={scopeOptions}
                  onPick={(value) => {
                    setScope(value)
                    pages.back()
                  }}
                />
              </div>
            </>
          )}
          {pages.page === 'resume' && (
            <>
              <DrillBack ref={pages.backRef} title={<Trans>Resume</Trans>} onBack={pages.back} />
              <DrillChoices
                label={t`Resume`}
                value={choice}
                options={resumeOptions.map((o) => ({ ...o, hint: resumeHint(o.value) }))}
                onPick={(value) => {
                  setChoice(value as ResumeChoice)
                  pages.back()
                }}
              />
            </>
          )}
        </DrillPanes>
        <Button fullWidth mt={6} loading={pause.isPending} disabled={invalid} onClick={submit}>
          <Trans>Pause</Trans>
        </Button>
      </Popover.Dropdown>
    </Popover>
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
                leftSection={<IconPlayerPlay size={16} />}
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
            Chapters already downloading finish. Torrents already handed to the download client keep downloading.
          </Trans>
        </Text>
      </Stack>
    </Alert>
  )
}
