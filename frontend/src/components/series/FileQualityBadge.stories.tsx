import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { fileQuality } from '../../../.storybook/mocks/series'
import { FileQualityBadge } from './FileQualityBadge'

const meta = {
  component: FileQualityBadge,
  tags: ['ai-generated'],
  args: { quality: fileQuality(1) },
} satisfies Meta<typeof FileQualityBadge>

export default meta
type Story = StoryObj<typeof meta>

export const Scanlator: Story = {
  play: async ({ canvas }) => {
    await expect(canvas.getByText(/Scanlator/)).toBeVisible()
  },
}

export const Official: Story = {
  args: {
    quality: fileQuality(2, { tier: 'official', group: null, score: 91, cutoffMet: true, imageFormat: 'webp', medianWidth: 1600 }),
  },
}

export const Volume: Story = {
  args: { quality: fileQuality(3, { tier: 'volume', group: null, score: 120, cutoffMet: true, medianWidth: 2000 }) },
}

export const Aggregator: Story = {
  args: { quality: fileQuality(4, { tier: 'aggregator', group: null, score: 31, medianWidth: 800, imageFormat: 'png' }) },
}

export const BelowCutoff: Story = {
  args: { quality: fileQuality(5, { cutoffMet: false, score: 40 }) },
}

export const Protected: Story = {
  args: { quality: fileQuality(6, { trusted: true, cutoffMet: true }) },
}

export const Unmeasured: Story = {
  args: {
    quality: fileQuality(7, { tier: 'scanlator', measured: false, medianWidth: null, pageCount: null, score: null, cutoffMet: null }),
  },
}

export const NothingToShow: Story = {
  args: { quality: fileQuality(8, { tier: 'unknown', measured: false }) },
  play: async ({ canvasElement }) => {
    await expect(canvasElement.querySelector('#storybook-root')?.children.length ?? 0).toBe(0)
  },
}
