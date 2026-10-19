import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { SettingsHelp } from './SettingsHelp'

const meta = {
  component: SettingsHelp,
  tags: ['ai-generated'],
  parameters: { layout: 'padded' },
  decorators: [
    (Story) => (
      <div style={{ maxWidth: 520 }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof SettingsHelp>

export default meta
type Story = StoryObj<typeof meta>

export const Short: Story = {
  args: { children: 'Folders where series are stored.' },
  play: async ({ canvas }) => {
    await canvas.findByText('Folders where series are stored.')
    await expect(canvas.queryByRole('button', { name: 'More' })).toBeNull()
  },
}

export const LongCollapsedThenExpanded: Story = {
  args: {
    children:
      'Series metadata comes from a local copy of the catalogue that is refreshed from the nightly dump. It lets search and imports skip the rate limit of the remote API, and Discover needs it. The remote API is used until the first download finishes, so a fresh install works straight away but searches a little slower until the copy lands.',
  },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'More' }))
    await expect(await canvas.findByRole('button', { name: 'Less' })).toBeVisible()
  },
}
