import { useId, useLayoutEffect, useRef, type ReactNode } from 'react'

/**
 * A utility modal's title with one line under it saying what the dialog is for. Pass as the Modal's
 * `title`. Mantine names the dialog by the whole title element, so the dialog's `aria-labelledby`
 * is pointed at the title text alone and the line is added to `aria-describedby`.
 */
export function ModalHeading({ title, description }: { title: ReactNode; description?: ReactNode }) {
  const titleId = useId()
  const descriptionId = useId()
  const ref = useRef<HTMLSpanElement>(null)
  const hasDescription = Boolean(description)

  useLayoutEffect(() => {
    const dialog = ref.current?.closest<HTMLElement>('[role="dialog"]')
    if (!dialog || !hasDescription) return
    const apply = () => {
      if (dialog.getAttribute('aria-labelledby') !== titleId) dialog.setAttribute('aria-labelledby', titleId)
      const ids = (dialog.getAttribute('aria-describedby') ?? '').split(/\s+/).filter(Boolean)
      if (!ids.includes(descriptionId)) dialog.setAttribute('aria-describedby', [...ids, descriptionId].join(' '))
    }
    apply()
    const observer = new MutationObserver(apply)
    observer.observe(dialog, { attributes: true, attributeFilter: ['aria-labelledby', 'aria-describedby'] })
    return () => {
      observer.disconnect()
      const ids = (dialog.getAttribute('aria-describedby') ?? '').split(/\s+/).filter((id) => id && id !== descriptionId)
      if (ids.length) dialog.setAttribute('aria-describedby', ids.join(' '))
      else dialog.removeAttribute('aria-describedby')
    }
  }, [hasDescription, titleId, descriptionId])

  return (
    <>
      <span ref={ref} id={titleId} className="modal-heading-title">
        {title}
      </span>
      {description && (
        <span id={descriptionId} className="modal-heading-description">
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
