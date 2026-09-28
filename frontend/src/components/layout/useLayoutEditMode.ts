import { useCallback } from 'react'
import { useSearchParams } from 'react-router-dom'

/**
 * Whether the page is in layout edit mode, kept in the URL (`?edit=1`) so Settings can link
 * straight into it. Entering and leaving replace the history entry, so Back leaves the page rather
 * than toggling the mode.
 */
export function useLayoutEditMode(allowed = true) {
  const [params, setParams] = useSearchParams()
  const editing = allowed && params.get('edit') === '1'

  const enter = useCallback(
    () =>
      setParams(
        (prev) => {
          const next = new URLSearchParams(prev)
          next.set('edit', '1')
          return next
        },
        { replace: true },
      ),
    [setParams],
  )
  const exit = useCallback(
    () =>
      setParams(
        (prev) => {
          const next = new URLSearchParams(prev)
          next.delete('edit')
          return next
        },
        { replace: true },
      ),
    [setParams],
  )

  return { editing, enter, exit }
}
