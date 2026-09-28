import { useCallback, useEffect, useRef, useState } from 'react'
import { notifications } from '@mantine/notifications'
import { t as now } from '@lingui/core/macro'
import { copyText } from '../../lib/clipboard'

/**
 * Stand-in for Mantine's `CopyButton`/`useClipboard`, which only call `navigator.clipboard` and
 * silently do nothing over plain HTTP. Goes through {@link copyText} and says so when copying fails.
 */
export function useCopyText(timeout = 1500) {
  const [copied, setCopied] = useState(false)
  const timer = useRef<number | undefined>(undefined)

  useEffect(() => () => window.clearTimeout(timer.current), [])

  const copy = useCallback(
    async (text: string) => {
      const ok = await copyText(text)
      window.clearTimeout(timer.current)
      setCopied(ok)
      if (ok) timer.current = window.setTimeout(() => setCopied(false), timeout)
      else
        notifications.show({
          message: now`Could not copy to the clipboard. Select the text and copy it by hand.`,
          color: 'var(--danger)',
        })
      return ok
    },
    [timeout],
  )

  return { copied, copy }
}
