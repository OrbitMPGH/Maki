import { useState } from 'react'
import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { NamingFormatInput } from './NamingFormatInput'

const meta = {
  component: NamingFormatInput,
  tags: ['ai-generated'],
  parameters: { layout: 'padded' },
  args: {
    label: 'Series folder format',
    description: 'Used when adding a series, importing one, or renaming its folder',
    value: '{Series TitleYear}',
    example: 'Lanterns Over Kasumi (2021)',
    onChange: () => {},
    onCommit: fn(),
  },
  render: function Render(args) {
    const [value, setValue] = useState(args.value)
    return (
      <div style={{ maxWidth: 520 }}>
        <NamingFormatInput {...args} value={value} onChange={setValue} />
      </div>
    )
  },
} satisfies Meta<typeof NamingFormatInput>

export default meta
type Story = StoryObj<typeof meta>

export const WithExample: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Example: Lanterns Over Kasumi (2021)')).toBeVisible()
  },
}

export const WithError: Story = {
  args: { value: '{Series Title', example: undefined, error: 'The token is not closed.' },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('The token is not closed.')).toBeVisible()
  },
}

export const CommitsOnBlur: Story = {
  play: async ({ args, canvas, userEvent }) => {
    const field = await canvas.findByRole('textbox', { name: 'Series folder format' })
    await userEvent.type(field, ' x')
    await userEvent.tab()
    await waitFor(() => expect(args.onCommit).toHaveBeenCalled())
  },
}

export const TokenPickerInsertsTokens: Story = {
  play: async ({ args, canvas, canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    await userEvent.click(await canvas.findByRole('button', { name: 'Series folder format tokens' }))
    const dialog = await body.findByRole('dialog')
    await waitFor(() => expect(dialog).toBeVisible())
    await userEvent.click(await body.findByText('Aoi Mizuhara'))
    await userEvent.keyboard('{Escape}')
    await waitFor(() => expect(args.onCommit).toHaveBeenCalled())
    const field = (await canvas.findByRole('textbox', { name: 'Series folder format' })) as HTMLInputElement
    await expect(field.value).toContain('{Series Author}')
  },
}
