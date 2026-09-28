import { useLayoutEffect, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import { Anchor, Box, Text } from '@mantine/core'
import type { MantineSpacing } from '@mantine/core'
import { Trans } from '@lingui/react/macro'

/**
 * The explanation under a settings card title. Two lines, with the rest one click away: the full
 * paragraph is there for whoever needs it, without every card opening with a wall of text.
 */
export function SettingsHelp({ children, mb }: { children: ReactNode; mb?: MantineSpacing }) {
  const ref = useRef<HTMLParagraphElement>(null)
  const [open, setOpen] = useState(false)
  const [overflows, setOverflows] = useState(false)

  useLayoutEffect(() => {
    const el = ref.current
    if (!el) return
    const measure = () => setOverflows(el.scrollHeight > el.clientHeight + 1)
    measure()
    const observer = new ResizeObserver(measure)
    observer.observe(el)
    return () => observer.disconnect()
  }, [])

  return (
    <Box mb={mb}>
      <Text ref={ref} size="sm" c="var(--ink-3)" lineClamp={open ? undefined : 2}>
        {children}
      </Text>
      {(overflows || open) && (
        <Anchor component="button" type="button" size="xs" mt={2} onClick={() => setOpen((o) => !o)}>
          {open ? <Trans>Less</Trans> : <Trans>More</Trans>}
        </Anchor>
      )}
    </Box>
  )
}
