import type { ReactNode } from 'react'
import { Button, Group, Modal, Stack, Text } from '@mantine/core'
import { Trans } from '@lingui/react/macro'

/**
 * The one question before something that cannot be taken back: what is about to go, what follows
 * from it, and a danger-coloured button that names the action. Same shape as the Activity page's
 * "Clear queue" dialog, so every irreversible action in the app asks the same way.
 *
 * Policy: anything that cascades or cannot be recreated by typing a name again asks first (tags,
 * root folders, goals, rails, profiles, keys). A saved filter or preset is cheap to rebuild, so those
 * delete at once and offer an Undo toast instead.
 */
export function ConfirmDialog({
  opened,
  onClose,
  title,
  children,
  confirmLabel,
  onConfirm,
  loading,
  confirmDisabled,
}: {
  opened: boolean
  onClose: () => void
  title: ReactNode
  children: ReactNode
  confirmLabel: ReactNode
  onConfirm: () => void
  loading?: boolean
  /** Holds the button back, e.g. once the server refused the recycle bin and the body offers a permanent delete instead. */
  confirmDisabled?: boolean
}) {
  return (
    <Modal opened={opened} onClose={onClose} title={title} centered attributes={{ content: { 'data-edge': 'danger' } }}>
      <Stack gap="sm">
        {/* `component="div"`: a caller that needs a form control (a NumberInput, say) alongside
            the confirmation copy can't nest one inside a `<p>`, which is what Text renders by
            default. */}
        <Text size="sm" component="div">{children}</Text>
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>
            <Trans>Cancel</Trans>
          </Button>
          <Button color="var(--danger-fill)" loading={loading} disabled={confirmDisabled} onClick={onConfirm}>
            {confirmLabel}
          </Button>
        </Group>
      </Stack>
    </Modal>
  )
}
