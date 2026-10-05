import { useEffect, useRef } from 'react'
import type { CSSProperties } from 'react'
import { Button, Loader, Stack, Text } from '@mantine/core'
import { notifications } from '@mantine/notifications'
import { IconBook, IconCircleCheckFilled, IconRefresh } from '@tabler/icons-react'
import { Trans, useLingui } from '@lingui/react/macro'
import { releaseSeriesPreview, useSeriesPreview, useStartSeriesPreview } from '../../api/preview'

/**
 * Starts fetching chapter 1 on click and leaves the user on the card while it runs, so they can read
 * the tags and reviews in the meantime. Once the pages are in, the same button opens the reader.
 * Mounted per series: unmounting (card closed, rerolled) releases the job.
 */
export function PreviewChapterButton({
  providerId,
  title,
  onRead,
}: {
  providerId: string
  title: string
  onRead: () => void
}) {
  const { t } = useLingui()
  const { mutate: start, error: startError, isPending: starting, isSuccess: started } = useStartSeriesPreview()
  const { data: preview, isError: lost } = useSeriesPreview(providerId, started)

  // Released on a timer so StrictMode's mount, unmount, mount in dev keeps the job alive.
  const releaseTimer = useRef<number | undefined>(undefined)
  const notificationId = `series-preview-${providerId}`
  useEffect(() => {
    window.clearTimeout(releaseTimer.current)
    return () => {
      notifications.hide(notificationId)
      releaseTimer.current = window.setTimeout(() => releaseSeriesPreview(providerId), 0)
    }
  }, [providerId, notificationId])

  const status = starting ? 'starting' : started ? preview?.status : undefined
  const failure =
    startError?.message ??
    (lost ? t`The preview expired. Try again.` : null) ??
    (status === 'failed' ? (preview?.error ?? t`No source could serve the first chapter.`) : null)

  const read = () => {
    notifications.hide(notificationId)
    onRead()
  }

  // Only a job that finished while the user waited gets a notification. One that was already
  // ready when the button was pressed (the server keeps finished previews for an hour) opens
  // straight away, since there is nothing to announce.
  const lastStatus = useRef<string | undefined>(undefined)
  useEffect(() => {
    const previous = lastStatus.current
    lastStatus.current = status
    if (status !== 'ready' || previous === 'ready') return
    if (previous === 'starting') {
      onRead()
      return
    }
    const chapterLabel = preview?.chapterLabel
    notifications.show({
      id: notificationId,
      color: 'var(--ok)',
      icon: <IconCircleCheckFilled size={16} />,
      title: t`First chapter ready`,
      message: (
        <Stack gap={6} align="flex-start">
          <Text size="sm">
            {chapterLabel ? (
              <Trans>
                Chapter {chapterLabel} of {title} is ready to read.
              </Trans>
            ) : (
              <Trans>The first chapter of {title} is ready to read.</Trans>
            )}
          </Text>
          <Button size="compact-xs" variant="light" onClick={read}>
            <Trans>Read now</Trans>
          </Button>
        </Stack>
      ),
    })
    // `read` and `onRead` are fresh every render; only the transition matters here.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [status])

  const source = preview?.sourceDisplayName ?? null
  const pageCount = preview?.pageCount ?? 0
  const donePages = preview?.ready.filter(Boolean).length ?? 0
  const working = !failure && (status === 'starting' || status === 'searching' || status === 'fetching')

  let label: React.ReactNode
  let caption: React.ReactNode = null
  let progress: number | null = null
  if (failure) {
    label = <Trans>Try the preview again</Trans>
    caption = failure
  } else if (status === 'ready') {
    label = <Trans>Read first chapter</Trans>
    caption = source ? <Trans>Downloaded from {source}</Trans> : null
  } else if (status === 'fetching' && pageCount > 0) {
    label = (
      <Trans>
        Downloading, {donePages} of {pageCount} pages
      </Trans>
    )
    caption = source ? <Trans>From {source}. You can keep browsing while it downloads.</Trans> : null
    progress = donePages / pageCount
  } else if (working) {
    label = <Trans>Finding a source…</Trans>
    caption = <Trans>You can keep browsing while it looks.</Trans>
  } else {
    label = <Trans>Preview first chapter</Trans>
  }

  const onClick = () => {
    if (working) return
    if (status === 'ready' && !failure) read()
    else start(providerId)
  }

  return (
    <Stack gap={4}>
      <Button
        className="preview-chapter-button"
        data-working={working || undefined}
        data-indeterminate={working && progress == null ? true : undefined}
        aria-disabled={working || undefined}
        variant={status === 'ready' && !failure ? 'filled' : 'default'}
        fullWidth
        leftSection={
          working ? (
            <Loader size={14} color="var(--brand)" />
          ) : failure ? (
            <IconRefresh size={16} />
          ) : (
            <IconBook size={16} />
          )
        }
        style={progress != null ? ({ '--preview-progress': `${Math.round(progress * 100)}%` } as CSSProperties) : undefined}
        onClick={onClick}
      >
        <span className="tnum">{label}</span>
      </Button>
      <Text
        size="xs"
        ta="center"
        c={failure ? 'var(--danger)' : 'var(--ink-4)'}
        aria-live="polite"
      >
        {caption}
      </Text>
    </Stack>
  )
}
