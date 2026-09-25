import type { ReactNode } from 'react'
import { Button, Group, Modal, Stack, Text } from '@mantine/core'
import { Trans } from '@lingui/react/macro'

/**
 * The one question before something that cannot be taken back: what is about to go, what follows
 * from it, and a danger-coloured button that names the action. Same shape as the Activity page's
 * "Clear queue" dialog, so every irreversible action in the app asks the same way.
 */
export function ConfirmDialog({
  opened,
  onClose,
  title,
  children,
  confirmLabel,
  onConfirm,
  loading,
}: {
  opened: boolean
  onClose: () => void
  title: ReactNode
  children: ReactNode
  confirmLabel: ReactNode
  onConfirm: () => void
  loading?: boolean
}) {
  return (
    <Modal opened={opened} onClose={onClose} title={title} centered>
      <Stack gap="sm">
        {/* `component="div"`: a caller that needs a form control (a NumberInput, say) alongside
            the confirmation copy can't nest one inside a `<p>`, which is what Text renders by
            default. */}
        <Text size="sm" component="div">{children}</Text>
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>
            <Trans>Cancel</Trans>
          </Button>
          <Button color="var(--danger-fill)" loading={loading} onClick={onConfirm}>
            {confirmLabel}
          </Button>
        </Group>
      </Stack>
    </Modal>
  )
}
