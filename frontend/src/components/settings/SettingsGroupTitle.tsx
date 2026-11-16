import type { ReactNode } from 'react'
import { Text } from '@mantine/core'
import type { MantineSpacing } from '@mantine/core'

/** The title of a group of fields inside a settings card, one level under the card's own title. */
export function SettingsGroupTitle({
  children,
  mb,
  mt,
}: {
  children: ReactNode
  mb?: MantineSpacing
  mt?: MantineSpacing
}) {
  return (
    <Text size="sm" fw={500} mb={mb} mt={mt}>
      {children}
    </Text>
  )
}
