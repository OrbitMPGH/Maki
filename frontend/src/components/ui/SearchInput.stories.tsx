import { useState } from 'react'
import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, userEvent } from 'storybook/test'
import { SearchInput, type SearchInputProps } from './SearchInput'

function Controlled(args: SearchInputProps) {
  const [value, setValue] = useState(args.value)
  return <SearchInput {...args} value={value} onChange={(e) => setValue(e.currentTarget.value)} onClear={() => setValue('')} />
}

const meta = {
  component: SearchInput,
  tags: ['ai-generated'],
  args: { value: '', onClear: () => {}, placeholder: 'Search titles', 'aria-label': 'Search titles' },
  render: (args) => <Controlled {...args} />,
} satisfies Meta<typeof SearchInput>

export default meta
type Story = StoryObj<typeof meta>

export const Empty: Story = {
  play: async ({ canvas }) => {
    await expect(canvas.queryByRole('button', { name: 'Clear search' })).toBeNull()
  },
}

export const ClearsText: Story = {
  play: async ({ canvas }) => {
    const input = await canvas.findByRole('textbox', { name: 'Search titles' })
    await userEvent.type(input, 'berserk')
    await userEvent.click(await canvas.findByRole('button', { name: 'Clear search' }))
    await expect(input).toHaveValue('')
  },
}
