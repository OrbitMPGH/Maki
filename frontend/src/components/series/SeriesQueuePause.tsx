import { Button, Menu, Tooltip } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { IconPlayerPause, IconPlayerPlay } from '../../lib/icons'
import { notifications } from '../../lib/toast'
import { usePauseSeriesQueue, useQueuePause, useResumeSeriesQueue } from '../../api/hooks'
import { isSeriesPaused } from '../../api/queue'
import { useAuth } from '../../auth/AuthProvider'

function useSeriesQueuePause(seriesId: number) {
  const { t } = useLingui()
  const canManage = useAuth().can('ManageDownloadQueue')
  const paused = isSeriesPaused(useQueuePause().data, seriesId)
  const pause = usePauseSeriesQueue()
  const resume = useResumeSeriesQueue()
  const toggle = () => {
    const onError = () =>
      notifications.show({
        message: paused ? t`Couldn't resume the downloads` : t`Couldn't pause the downloads`,
        color: 'var(--danger)',
      })
    if (paused) resume.mutate(seriesId, { onError })
    else pause.mutate({ seriesId }, { onError })
  }
  return { canManage, paused, toggle, pending: pause.isPending || resume.isPending }
}

/**
 * The series page's sign that its downloads are paused, which is also the way to resume them. Nothing
 * while they run: pausing lives in the download menu, next to the actions that queue chapters.
 */
export function SeriesPausedButton({ seriesId }: { seriesId: number }) {
  const { t } = useLingui()
  const { canManage, paused, toggle, pending } = useSeriesQueuePause(seriesId)
  if (!paused) return null
  return (
    <Tooltip
      label={t`Downloads for this series are paused. Queued chapters wait until you resume; other series keep downloading.`}
      withArrow
      multiline
      w={260}
    >
      <Button
        variant="light"
        color="var(--warn)"
        size="md"
        radius="md"
        leftSection={<IconPlayerPlay size={17} />}
        loading={pending}
        data-disabled={!canManage || undefined}
        aria-disabled={!canManage || undefined}
        onClick={() => {
          if (canManage) toggle()
        }}
      >
        <Trans>Resume downloads</Trans>
      </Button>
    </Tooltip>
  )
}

/** "Pause downloads for this series" in the download menu, for whoever may pause the queue. */
export function SeriesPauseMenuItem({ seriesId }: { seriesId: number }) {
  const { canManage, paused, toggle } = useSeriesQueuePause(seriesId)
  if (!canManage) return null
  return (
    <>
      <Menu.Divider />
      <Menu.Item
        leftSection={paused ? <IconPlayerPlay size={16} /> : <IconPlayerPause size={16} />}
        onClick={toggle}
      >
        {paused ? <Trans>Resume downloads for this series</Trans> : <Trans>Pause downloads for this series</Trans>}
      </Menu.Item>
    </>
  )
}
