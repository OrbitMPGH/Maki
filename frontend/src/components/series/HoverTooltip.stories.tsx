import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, within } from 'storybook/test'
import { Badge } from '@mantine/core'
import { HoverTooltip } from './HoverTooltip'

const meta = {
  component: HoverTooltip,
  tags: ['ai-generated'],
  args: {
    label: 'Downloaded from MangaDex, 24 pages at 1200px',
    children: <Badge>Ch.12</Badge>,
  },
} satisfies Meta<typeof HoverTooltip>

export default meta
type Story = StoryObj<typeof meta>

export const OpensOnHover: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.hover(canvas.getByText('Ch.12'))
    const body = within(canvasElement.ownerDocument.body)
    await expect(await body.findByText(/24 pages at 1200px/)).toBeVisible()
  },
}

export const Disabled: Story = {
  args: { disabled: true },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.hover(canvas.getByText('Ch.12'))
    await expect(within(canvasElement.ownerDocument.body).queryByText(/24 pages/)).toBeNull()
  },
}
