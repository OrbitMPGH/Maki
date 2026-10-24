import type { ReactNode } from 'react'
import { Text } from '@mantine/core'
import type { MantineSpacing } from '@mantine/core'

/** The explanation under a settings card title or above a group of fields, always in full. */
export function SettingsHelp({ children, mb }: { children: ReactNode; mb?: MantineSpacing }) {
  return (
    <Text size="sm" c="var(--ink-3)" mb={mb}>
      {children}
    </Text>
  )
}
