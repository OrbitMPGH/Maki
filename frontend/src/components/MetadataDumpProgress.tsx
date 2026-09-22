import { useEffect, useRef } from 'react'
import { Group, Progress, Stack, Text } from '@mantine/core'
import { notifications } from '@mantine/notifications'
import { useQueryClient } from '@tanstack/react-query'
import { DUMP_PROGRESS_KEY, useDumpProgress, type DumpProgress } from '../api/hooks'
import { useHubEvent } from '../api/signalr'
import { formatBytes, formatReadingTime } from '../format'
import { msg, t as now } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { useLabel } from '../i18n-context'

const TOAST_ID = 'mangabaka-dump'

/** Null while there is nothing to divide by, which is what leaves the bar indeterminate. */
export function dumpPercent(progress: DumpProgress): number | null {
  if (progress.phase !== 'downloading' || !progress.totalBytes) return null
  return Math.min(100, (progress.downloadedBytes / progress.totalBytes) * 100)
}

const DUMP_PHASE_LABELS: Record<string, MessageDescriptor> = {
  checking: msg`Checking for a new metadata snapshot`,
  downloading: msg`Downloading metadata database`,
  indexing: msg`Building metadata search indexes`,
  installing: msg`Installing metadata database`,
}

export function dumpPhaseLabel(phase: string): MessageDescriptor {
  return DUMP_PHASE_LABELS[phase] ?? msg`Metadata database`
}

/** "217.4 MB of 348.9 MB · 4.2 MB/s · 31s left", dropping whatever the server didn't report. */
export function dumpDetail(progress: DumpProgress): string {
  if (progress.phase !== 'downloading') {
    // Neither of the post-download phases has a measurable unit of work, so a byte count or an
    // ETA here would be the download's, frozen.
    return now`This takes a minute or two and only happens once per snapshot.`
  }

  const downloaded = formatBytes(progress.downloadedBytes)
  const total = progress.totalBytes ? formatBytes(progress.totalBytes) : null
  const parts = [total ? now`${downloaded} of ${total}` : downloaded]
  if (progress.bytesPerSecond) {
    const speed = formatBytes(progress.bytesPerSecond)
    parts.push(now`${speed}/s`)
  }
  if (progress.estimatedSecondsRemaining !== null) {
    const eta = formatReadingTime(progress.estimatedSecondsRemaining)
    parts.push(now`${eta} left`)
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
  const renderLabel = useLabel()
  const percent = dumpPercent(progress)
  const phaseLabel = renderLabel(dumpPhaseLabel(progress.phase))
  return (
    <Stack gap={6}>
      <Group justify="space-between" gap="xs" wrap="nowrap">
        <Text size="sm" fw={500}>
          {phaseLabel}
        </Text>
        {percent !== null && (
          <Text size="sm" c="var(--ink-3)">
            {percent.toFixed(0)}%
          </Text>
        )}
      </Group>
      <Progress
        value={percent ?? 100}
        animated={percent === null}
        size="sm"
        radius="xl"
        aria-label={phaseLabel}
      />
      <Text size="xs" c="var(--ink-3)">
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
        // The sentence is Maki's own and is translated; only the download job's own error text
        // rides along untranslated, appended rather than interpolated into the message so the
        // catalogue entry carries no placeholder for somebody else's words.
        message: `${now`Metadata database download failed:`} ${progress.lastError}`,
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
        message: `${now`Metadata database ready.`} ${now`Discover and offline search are available now.`}`,
      })
      // dumpPresent has just flipped, and the nav gates the Discover tab on it.
      void queryClient.invalidateQueries({ queryKey: ['settings', 'metadata'] })
      return
    }

    notifications.hide(TOAST_ID)
  }, [progress, queryClient])

  return null
}
