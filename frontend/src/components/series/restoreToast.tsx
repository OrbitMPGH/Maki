import { notifications } from '@mantine/notifications'
import { Plural, Trans } from '@lingui/react/macro'
import type { RestoreSeriesResponse } from '../../api/types'

/** The toast after restoring one series: how many removed chapters went back in the queue, or why none did. */
export function showRestoreResult({ queued, queueError }: RestoreSeriesResponse, redownload: boolean) {
  if (queueError) {
    notifications.show({
      color: 'var(--warn)',
      autoClose: 8000,
      message: (
        <Trans context="series">Restored to the library, but the removed chapters were not queued: {queueError}</Trans>
      ),
    })
    return
  }
  notifications.show({
    color: 'var(--ok)',
    message: redownload ? (
      <Plural
        value={queued}
        context="series"
        one="Restored to the library, # chapter queued"
        other="Restored to the library, # chapters queued"
      />
    ) : (
      <Trans context="series">Restored to the library</Trans>
    ),
  })
}

/** Follows a bulk restore's own progress toast when chapters were to be downloaded again. */
export function showBulkRestoreQueued(queued: number, queueError: string | null) {
  if (queueError) {
    notifications.show({
      color: 'var(--warn)',
      autoClose: 8000,
      message: <Trans>Some removed chapters were not queued: {queueError}</Trans>,
    })
    return
  }
  notifications.show({
    color: 'var(--ok)',
    message: <Plural value={queued} one="Queued # removed chapter" other="Queued # removed chapters" />,
  })
}
