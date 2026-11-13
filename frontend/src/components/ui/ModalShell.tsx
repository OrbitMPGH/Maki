import type { ReactNode } from 'react'

/**
 * A utility modal's title with one line under it saying what the dialog is for. Pass as the Modal's
 * `title`. The line is a visual lead-in, kept out of the heading's accessible name so the dialog is
 * still announced by its title alone; Mantine describes the dialog by its body.
 */
export function ModalHeading({ title, description }: { title: ReactNode; description?: ReactNode }) {
  return (
    <>
      <span className="modal-heading-title">{title}</span>
      {description && (
        <span className="modal-heading-description" aria-hidden>
          {description}
        </span>
      )}
    </>
  )
}

/**
 * The bar along a utility modal's foot: a status line on the left, the actions on the right, ruled
 * off by a hairline. It stays pinned while the body scrolls, so the main action never scrolls away.
 * Render it as the body's last child.
 */
export function ModalFooter({ status, children }: { status?: ReactNode; children?: ReactNode }) {
  return (
    <div className="modal-footer">
      <div className="modal-footer-status">{status}</div>
      {children && <div className="modal-footer-actions">{children}</div>}
    </div>
  )
}

/** The shared one-line Note, under the name the dialogs already use. */
export { Note as ModalNote } from './Note'
