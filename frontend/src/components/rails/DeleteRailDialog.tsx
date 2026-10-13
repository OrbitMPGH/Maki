import { Trans } from '@lingui/react/macro'
import { t as now } from '@lingui/core/macro'
import { notifications } from '@mantine/notifications'
import { useDeleteCustomRail } from '../../api/customRails'
import { ConfirmDialog } from '../ui/ConfirmDialog'

export function DeleteRailDialog({
  opened,
  onClose,
  railId,
  railName,
  onDeleted,
}: {
  opened: boolean
  onClose: () => void
  railId: number | undefined
  railName: string
  onDeleted?: () => void
}) {
  const remove = useDeleteCustomRail()

  return (
    <ConfirmDialog
      opened={opened}
      onClose={onClose}
      title={<Trans>Delete rail</Trans>}
      confirmLabel={<Trans>Delete</Trans>}
      loading={remove.isPending}
      onConfirm={() =>
        railId !== undefined &&
        remove.mutate(railId, {
          onSuccess: () => {
            onClose()
            onDeleted?.()
            notifications.show({ color: 'var(--ok)', message: now`Rail deleted` })
          },
        })
      }
    >
      <Trans>Delete the rail "{railName}"? Its filters go with it.</Trans>
    </ConfirmDialog>
  )
}
