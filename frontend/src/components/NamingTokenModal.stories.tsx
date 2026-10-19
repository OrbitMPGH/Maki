import { useState } from 'react'
import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { NamingTokenModal } from './NamingTokenModal'

const meta = {
  component: NamingTokenModal,
  tags: ['ai-generated'],
  parameters: { layout: 'fullscreen' },
  args: { opened: true, onClose: fn(), format: '{Series TitleYear}', onChange: () => {} },
  render: function Render(args) {
    const [format, setFormat] = useState(args.format)
    return <NamingTokenModal {...args} format={format} onChange={setFormat} />
  },
} satisfies Meta<typeof NamingTokenModal>

export default meta
type Story = StoryObj<typeof meta>

export const Opened: Story = {
  play: async ({ canvasElement }) => {
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog')
    await waitFor(() => expect(dialog).toBeVisible())
    await expect(await body.findByText('{Series Author}')).toBeVisible()
  },
}

export const PickingATokenAppendsIt: Story = {
  args: { format: '{Series Title} - ' },
  play: async ({ canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    const dialog = within(await body.findByRole('dialog'))
    await userEvent.click(await dialog.findByText('12'))
    await expect(dialog.getByDisplayValue(/\{Series Title\} - .*\{Chapter\}/)).toBeVisible()
  },
}

export const Closed: Story = {
  args: { opened: false },
}
