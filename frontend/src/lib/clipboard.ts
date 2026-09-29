/**
 * `navigator.clipboard` only exists in secure contexts, so a self-hosted instance reached over
 * plain HTTP on a LAN address has none. `execCommand('copy')` still works there.
 */
export async function copyText(text: string): Promise<boolean> {
  if (typeof navigator.clipboard?.writeText === 'function') {
    try {
      await navigator.clipboard.writeText(text)
      return true
    } catch {
      // fall through to the legacy path
    }
  }
  // Inside the open dialog when there is one: a modal's focus trap pulls focus back from anything
  // outside it, and copying needs the textarea focused.
  const previous = document.activeElement as HTMLElement | null
  const host = previous?.closest('[role="dialog"]') ?? document.body
  const area = document.createElement('textarea')
  area.value = text
  area.readOnly = true
  area.style.position = 'fixed'
  area.style.opacity = '0'
  host.appendChild(area)
  area.focus()
  area.select()
  try {
    return document.execCommand('copy')
  } catch {
    return false
  } finally {
    host.removeChild(area)
    previous?.focus?.()
  }
}
