import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { RouteErrorBoundary } from './RouteErrorBoundary'

function BrokenPage(): never {
  throw new Error('Series 14 has no title')
}

const meta = {
  component: RouteErrorBoundary,
  tags: ['ai-generated'],
  parameters: { layout: 'fullscreen' },
} satisfies Meta<typeof RouteErrorBoundary>

export default meta
type Story = StoryObj<typeof meta>

export const Healthy: Story = {
  args: { children: <p style={{ padding: 24 }}>The page rendered normally.</p> },
  play: async ({ canvas }) => {
    await expect(canvas.getByText('The page rendered normally.')).toBeVisible()
  },
}

export const ChildThrows: Story = {
  args: { children: <BrokenPage /> },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { name: 'This page hit an error' })).toBeVisible()
    await expect(canvas.getByRole('button', { name: 'Reload' })).toBeVisible()
    await expect(canvas.getByRole('link', { name: 'Go to start page' })).toHaveAttribute('href', '/')
  },
}
