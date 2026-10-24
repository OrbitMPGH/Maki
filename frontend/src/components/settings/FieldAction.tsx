import type { ReactNode } from 'react'
import { Input } from '@mantine/core'

/**
 * The buttons at the end of a row of fields (Test, Save). They sit in an empty field wrapper so the
 * invisible label takes the same height as the label of the inputs beside them, whatever the theme's
 * label size is.
 */
export function FieldAction({ children }: { children: ReactNode }) {
  return (
    <Input.Wrapper label={' '} labelProps={{ 'aria-hidden': true }}>
      <div className="settings-field-action">{children}</div>
    </Input.Wrapper>
  )
}
