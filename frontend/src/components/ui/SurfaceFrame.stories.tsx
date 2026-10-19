import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { Text, Title } from '@mantine/core'
import { Panel } from './Panel'
import { SurfaceFrame } from './SurfaceFrame'

const meta = {
  component: SurfaceFrame,
  tags: ['ai-generated'],
  args: {
    children: (
      <>
        <Title order={1}>Library</Title>
        <Panel mt="md">
          <Text size="sm">The frame sets the page width and rhythm; the content inside is up to the page.</Text>
        </Panel>
      </>
    ),
  },
  parameters: { layout: 'fullscreen' },
} satisfies Meta<typeof SurfaceFrame>

export default meta
type Story = StoryObj<typeof meta>

export const WideStandard: Story = {
  play: async ({ canvasElement }) => {
    const frame = canvasElement.querySelector('.surface-frame')
    await expect(frame).toHaveClass('surface-frame--wide', 'surface-frame--standard')
  },
}

export const FullWidth: Story = {
  args: { width: 'full' },
}

export const Editorial: Story = {
  args: { pageStyle: 'editorial' },
  play: async ({ canvasElement }) => {
    await expect(canvasElement.querySelector('.surface-frame')).toHaveAttribute('data-page-style', 'editorial')
  },
}

export const Operational: Story = {
  args: { pageStyle: 'operational', width: 'full' },
}
