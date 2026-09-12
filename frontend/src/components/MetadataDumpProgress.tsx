import { useEffect, useRef } from 'react'
import { Group, Progress, Stack, Text } from '@mantine/core'
import { notifications } from '@mantine/notifications'
import { useQueryClient } from '@tanstack/react-query'
import { DUMP_PROGRESS_KEY, useDumpProgress, type DumpProgress } from '../api/hooks'
import { useHubEvent } from '../api/signalr'

const TOAST_ID = 'mangabaka-dump'

function formatBytes(bytes: number): string {
  const units = ['B', 'KB', 'MB', 'GB']
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit++
  }
  return `${value.toFixed(1)} ${units[unit]}`
}

function formatEta(seconds: number): string {
  if (seconds < 60) return `${seconds}s`
  const minutes = Math.round(seconds / 60)
  if (minutes < 60) return `${minutes} min`
  const hours = Math.floor(minutes / 60)
  return `${hours}h ${minutes % 60}m`
}

/** Null while there is nothing to divide by, which is what leaves the bar indeterminate. */
export function dumpPercent(progress: DumpProgress): number | null {
  if (progress.phase !== 'downloading' || !progress.totalBytes) return null
  return Math.min(100, (progress.downloadedBytes / progress.totalBytes) * 100)
}

export function dumpPhaseLabel(phase: string): string {
  switch (phase) {
    case 'checking':
      return 'Checking for a new metadata snapshot'
    case 'downloading':
      return 'Downloading metadata database'
    case 'indexing':
      return 'Building metadata search indexes'
    case 'installing':
      return 'Installing metadata database'
    default:
      return 'Metadata database'
  }
}

/** "217.4 MB of 348.9 MB · 4.2 MB/s · 31s left", dropping whatever the server didn't report. */
export function dumpDetail(progress: DumpProgress): string {
  if (progress.phase !== 'downloading') {
    // Neither of the post-download phases has a measurable unit of work, so a byte count or an
    // ETA here would be the download's, frozen.
    return 'This takes a minute or two and only happens once per snapshot.'
  }

  const parts = [
    progress.totalBytes
      ? `${formatBytes(progress.downloadedBytes)} of ${formatBytes(progress.totalBytes)}`
      : formatBytes(progress.downloadedBytes),
  ]
  if (progress.bytesPerSecond) parts.push(`${formatBytes(progress.bytesPerSecond)}/s`)
  if (progress.estimatedSecondsRemaining !== null) {
    parts.push(`${formatEta(progress.estimatedSecondsRemaining)} left`)
  }
  return parts.join(' · ')
}

/** The phases worth interrupting somebody for. "checking" runs every six hours and finds nothing. */
function worthShowing(progress: DumpProgress): boolean {
  return progress.running && progress.phase !== 'checking'
}

/**
 * Bar plus one line of detail, shared by the toast and the Metadata settings card so the two can
 * never describe the same transfer differently.
 */
export function DumpProgressBar({ progress }: { progress: DumpProgress }) {
  const percent = dumpPercent(progress)
  return (
    <Stack gap={6}>
      <Group justify="space-between" gap="xs" wrap="nowrap">
        <Text size="sm" fw={500}>
          {dumpPhaseLabel(progress.phase)}
        </Text>
        {percent !== null && (
          <Text size="sm" c="dimmed">
            {percent.toFixed(0)}%
          </Text>
        )}
      </Group>
      <Progress
        value={percent ?? 100}
        animated={percent === null}
        size="sm"
        radius="xl"
        aria-label={dumpPhaseLabel(progress.phase)}
      />
      <Text size="xs" c="dimmed">
        {dumpDetail(progress)}
      </Text>
    </Stack>
  )
}

/**
 * The live dump-download toast, mounted once for admins.
 *
 * <p>Renders nothing: it owns a notification rather than a piece of the page, so the progress
 * follows the user around the app instead of living on the settings tab they had to be looking at.
 * The refresh is a ~350 MB transfer plus an index build and Discover, search and imports are all
 * waiting on it, so a fresh install with no feedback reads as broken.</p>
 *
 * <p>State comes from the hub push, with one REST read on mount to catch up a page opened
 * mid-download. Dismissing it is remembered for that run only, keyed on when the run started, so
 * the next push doesn't drag it back.</p>
 */
export default function MetadataDumpProgress() {
  const queryClient = useQueryClient()
  const { data: progress } = useDumpProgress()
  const dismissedRun = useRef<string | null>(null)
  const shown = useRef(false)

  useHubEvent<DumpProgress>('dumpProgress', (payload) => {
    queryClient.setQueryData(DUMP_PROGRESS_KEY, payload)
  })

  useEffect(() => {
    if (!progress) return

    const runKey = progress.startedAt ?? 'unknown'

    if (worthShowing(progress)) {
      if (dismissedRun.current === runKey) return

      const body = {
        id: TOAST_ID,
        // Pinned bottom-right rather than inheriting the app's top-centre default: this one sits
        // there for the length of a 350 MB transfer, and the centre of the screen is where every
        // other toast appears for six seconds and leaves.
        position: 'bottom-right' as const,
        loading: true,
        autoClose: false as const,
        withCloseButton: true,
        onClose: () => {
          dismissedRun.current = runKey
          shown.current = false
        },
        message: <DumpProgressBar progress={progress} />,
      }

      if (shown.current) {
        notifications.update(body)
      } else {
        shown.current = true
        notifications.show(body)
      }
      return
    }

    if (!shown.current) return
    shown.current = false

    if (progress.lastError) {
      notifications.update({
        id: TOAST_ID,
        // update replaces the payload rather than merging it, so the position has to be repeated
        // or the closing frame jumps to the app's top-centre default.
        position: 'bottom-right',
        loading: false,
        color: 'red',
        autoClose: 10000,
        withCloseButton: true,
        message: `Metadata database download failed: ${progress.lastError}`,
      })
      return
    }

    if (progress.lastInstalled) {
      notifications.update({
        id: TOAST_ID,
        position: 'bottom-right',
        loading: false,
        color: 'green',
        autoClose: 8000,
        withCloseButton: true,
        message: 'Metadata database ready. Discover and offline search are available now.',
      })
      // dumpPresent has just flipped, and the nav gates the Discover tab on it.
      void queryClient.invalidateQueries({ queryKey: ['settings', 'metadata'] })
      return
    }

    notifications.hide(TOAST_ID)
  }, [progress, queryClient])

  return null
}
