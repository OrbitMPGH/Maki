import { createContext, useContext, useEffect, useId } from 'react'
import { Button } from '@mantine/core'
import { Trans } from '@lingui/react/macro'

/** Collects which settings cards hold unsaved edits, so the page can warn before dropping them. */
export const UnsavedSettingsContext = createContext<((id: string, dirty: boolean) => void) | null>(null)

/**
 * The one Save control for settings cards whose fields save together. Quiet while nothing has
 * changed, filled with an "Unsaved changes" note once something has. `dirty` left undefined is for
 * a card that cannot tell, which keeps the button enabled.
 */
export function SaveButton({
  dirty,
  loading,
  disabled,
  onClick,
}: {
  dirty?: boolean
  loading?: boolean
  disabled?: boolean
  onClick: () => void
}) {
  const id = useId()
  const report = useContext(UnsavedSettingsContext)

  useEffect(() => {
    report?.(id, dirty === true)
    return () => report?.(id, false)
  }, [report, id, dirty])

  return (
    <span className="save-control">
      {dirty && (
        <span className="save-control-hint">
          <Trans>Unsaved changes</Trans>
        </span>
      )}
      <Button
        variant={dirty === false ? 'default' : 'filled'}
        disabled={dirty === false || disabled}
        loading={loading}
        onClick={onClick}
      >
        <Trans>Save</Trans>
      </Button>
    </span>
  )
}
