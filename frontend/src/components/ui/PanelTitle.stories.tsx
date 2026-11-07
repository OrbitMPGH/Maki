import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { Text } from '@mantine/core'
import { IconFileText } from '../../lib/icons'
import { Panel } from './Panel'
import { PanelTitle } from './PanelTitle'

const meta = {
  component: PanelTitle,
  tags: ['ai-generated'],
  args: { children: 'Synopsis' },
  parameters: { layout: 'padded' },
  decorators: [
    (Story) => (
      <Panel maw={420}>
        <Story />
        <Text size="sm" c="var(--ink-3)" mt="sm">
          A night owl meets a vampire on the empty streets after midnight.
        </Text>
      </Panel>
    ),
  ],
} satisfies Meta<typeof PanelTitle>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    const title = await canvas.findByRole('heading', { level: 3, name: 'Synopsis' })
    await expect(title).toHaveClass('panel-title')
  },
}

export const WithIcon: Story = {
  args: { icon: IconFileText },
  play: async ({ canvasElement }) => {
    await expect(canvasElement.querySelector('.panel-title-icon')).not.toBeNull()
  },
}

export const AsH2: Story = {
  args: { order: 2 },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { level: 2, name: 'Synopsis' })).toBeVisible()
  },
}
