import type { ComponentPropsWithRef } from 'react'
import { ActionIcon, type ActionIconProps } from '@mantine/core'
import { useLingui } from '@lingui/react/macro'
import { IconDotsVertical } from '../../lib/icons'

/** The "more actions" trigger for a row, card or header menu. Pass `aria-label` to name what it opens. */
export function OverflowButton({ ref, ...rest }: ActionIconProps & ComponentPropsWithRef<'button'>) {
  const { t } = useLingui()
  return (
    <ActionIcon ref={ref} variant="subtle" color="var(--neutral)" aria-label={t`More actions`} {...rest}>
      <IconDotsVertical size={19} />
    </ActionIcon>
  )
}
