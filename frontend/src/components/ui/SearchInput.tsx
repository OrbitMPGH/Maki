import { forwardRef, type ReactNode } from 'react'
import { ActionIcon, TextInput, type TextInputProps } from '@mantine/core'
import { useLingui } from '@lingui/react/macro'
import { IconSearch, IconX } from '../../lib/icons'

export interface SearchInputProps extends Omit<TextInputProps, 'value' | 'type' | 'leftSection'> {
  value: string
  onClear: () => void
  /** Shown on the right while the box is empty, such as a shortcut hint. */
  idleSection?: ReactNode
}

/**
 * The one search and filter-by-text box: search icon on the left, a clear button on the right once
 * there is text. Size defaults to `sm`; only the big catalogue search and the command palette go larger.
 */
export const SearchInput = forwardRef<HTMLInputElement, SearchInputProps>(function SearchInput(
  { value, onClear, idleSection, size = 'sm', rightSection, ...rest },
  ref,
) {
  const { t } = useLingui()
  return (
    <TextInput
      ref={ref}
      type="text"
      autoComplete="off"
      enterKeyHint="search"
      size={size}
      value={value}
      leftSection={<IconSearch size={16} />}
      rightSectionPointerEvents={value ? 'all' : undefined}
      rightSection={
        value ? (
          <ActionIcon variant="subtle" color="var(--neutral)" size="sm" aria-label={t`Clear search`} onClick={onClear}>
            <IconX size={16} />
          </ActionIcon>
        ) : (
          (rightSection ?? idleSection)
        )
      }
      {...rest}
    />
  )
})
