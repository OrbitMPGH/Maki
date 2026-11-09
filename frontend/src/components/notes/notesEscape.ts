const TEXT_FIELD = 'textarea, input:not([type="checkbox"]):not([type="radio"])'

/**
 * Escape on an open notes surface in the reader. In a text field it only leaves the field, handing
 * focus to the surface's close button (or the surface) so a focus trap keeps it. A composer holding
 * unsaved text keeps the surface open, and a key pressed in another notes surface is left to that
 * one. Returns true when the surface should close.
 */
export function notesEscapeCloses(event: KeyboardEvent, surface: HTMLElement | null): boolean {
  const target = event.target instanceof HTMLElement ? event.target : null
  const within = target?.closest('.notes-surface')
  if (within && within !== surface && !surface?.contains(within)) return false
  if (surface && target && surface.contains(target) && target.matches(TEXT_FIELD)) {
    const focusTo = surface.querySelector<HTMLElement>('.notes-close') ?? surface
    focusTo.focus()
    return false
  }
  return !surface?.querySelector('.note-composer[data-dirty]')
}
