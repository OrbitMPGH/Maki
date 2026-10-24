import { useState } from 'react'
import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor } from 'storybook/test'
import { ContentRatingCards } from './ContentRatingCards'

const meta = {
  component: ContentRatingCards,
  tags: ['ai-generated'],
  args: { value: 'suggestive', onChange: () => {} },
  render: function Render(args) {
    const [value, setValue] = useState(args.value)
    return <ContentRatingCards value={value} onChange={setValue} />
  },
} satisfies Meta<typeof ContentRatingCards>

export default meta
type Story = StoryObj<typeof meta>

export const Suggestive: Story = {
  play: async ({ canvas, userEvent }) => {
    await waitFor(() => expect(canvas.getByRole('button', { name: /Suggestive/ })).toHaveAttribute('aria-pressed', 'true'))
    await userEvent.click(await canvas.findByRole('button', { name: /Erotica/ }))
    await waitFor(() => expect(canvas.getByRole('button', { name: /Erotica/ })).toHaveAttribute('aria-pressed', 'true'))
    await waitFor(() => expect(canvas.getByRole('button', { name: /Suggestive/ })).toHaveAttribute('aria-pressed', 'false'))
  },
}

export const SafeOnly: Story = {
  args: { value: 'safe' },
}

export const Everything: Story = {
  args: { value: 'pornographic' },
}
