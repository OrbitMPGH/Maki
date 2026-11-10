import type { ReactNode } from 'react'
import { Button, Modal, Stack, Text } from '@mantine/core'
import { Trans } from '@lingui/react/macro'
import { IconTrash } from '../../lib/icons'
import { ModalFooter, ModalHeading } from './ModalShell'

/**
 * The one question before something that cannot be taken back: what is about to go, what follows
 * from it, and a danger-coloured button that names the action. Same shape as the Activity page's
 * "Clear queue" dialog, so every irreversible action in the app asks the same way.
 *
 * Policy: anything that cascades or cannot be recreated by typing a name again asks first (tags,
 * root folders, goals, rails, profiles, keys). A saved filter or preset is cheap to rebuild, so those
 * delete at once and offer an Undo toast instead.
 *
 * `description` is the one-line consequence under the title; `children` is the optional body for
 * anything longer (a list of what is affected, a field). Callers that pass only `children` keep working.
 */
export function ConfirmDialog({
  opened,
  onClose,
  title,
  description,
  children,
  status,
  confirmLabel,
  onConfirm,
  loading,
  confirmDisabled,
}: {
  opened: boolean
  onClose: () => void
  title: ReactNode
  description?: ReactNode
  children?: ReactNode
  /** Left of the buttons in the footer, e.g. a count or what still has to be done. */
  status?: ReactNode
  confirmLabel: ReactNode
  onConfirm: () => void
  loading?: boolean
  /** Holds the button back, e.g. once the server refused the recycle bin and the body offers a permanent delete instead. */
  confirmDisabled?: boolean
}) {
  return (
    <Modal
      opened={opened}
      onClose={onClose}
      centered
      title={
        <span className="confirm-title">
          <span className="confirm-tile" aria-hidden>
            <IconTrash size={16} />
          </span>
          <span className="confirm-title-text">
            <ModalHeading title={title} description={description} />
          </span>
        </span>
      }
    >
      <Stack gap="sm">
        {/* `component="div"`: a caller that needs a form control (a NumberInput, say) alongside
            the confirmation copy can't nest one inside a `<p>`, which is what Text renders by
            default. */}
        {children && <Text size="sm" component="div">{children}</Text>}
        <ModalFooter status={status}>
          <Button variant="default" onClick={onClose}>
            <Trans>Cancel</Trans>
          </Button>
          <Button color="var(--danger-fill)" loading={loading} disabled={confirmDisabled} onClick={onConfirm}>
            {confirmLabel}
          </Button>
        </ModalFooter>
      </Stack>
    </Modal>
  )
}
