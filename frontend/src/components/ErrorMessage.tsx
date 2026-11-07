import { useId, useState } from 'react'
import { UnstyledButton } from '@mantine/core'
import { Trans } from '@lingui/react/macro'
import { ApiError } from '../api/client'
import { errorText } from '../api/errorText'
import { IconChevronDown } from '../lib/icons'

/**
 * A failure as a reader should see it. The server's own refusals are already worded for people and
 * show as they are; anything else (a network drop, a query library message with a JSON key in it)
 * gets a plain sentence, with the raw text behind a Details toggle for a bug report.
 */
export function ErrorMessage({ error }: { error: unknown }) {
  const [open, setOpen] = useState(false)
  const id = useId()
  if (error instanceof ApiError && error.message) return <span className="error-message">{error.message}</span>

  return (
    <span className="error-message" role="alert">
      <span>
        <Trans>Try again in a moment. If it keeps failing, the details help with a bug report.</Trans>
      </span>
      <UnstyledButton
        className="error-message-toggle"
        aria-expanded={open}
        aria-controls={id}
        onClick={() => setOpen((o) => !o)}
      >
        <Trans>Details</Trans>
        <IconChevronDown size={14} data-open={open || undefined} />
      </UnstyledButton>
      <code className="error-message-detail" id={id} hidden={!open}>
        {errorText(error)}
      </code>
    </span>
  )
}
