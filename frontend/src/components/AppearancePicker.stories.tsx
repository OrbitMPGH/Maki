import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { AppearancePicker } from './AppearancePicker'

const meta = {
  component: AppearancePicker,
  tags: ['ai-generated'],
} satisfies Meta<typeof AppearancePicker>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas, userEvent }) => {
    const accents = within(await canvas.findByRole('radiogroup', { name: 'Accent' }))
    const next = (await accents.findAllByRole('radio')).find((r) => r.getAttribute('aria-checked') === 'false')!
    await userEvent.click(next)
    await waitFor(() => expect(next).toHaveAttribute('aria-checked', 'true'))
  },
}

export const WithBackgroundHints: Story = {
  args: { backgroundHints: { tinted: { label: 'Default' }, night: { label: 'Dim', muted: true } } },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Dim')).toBeVisible()
  },
}
