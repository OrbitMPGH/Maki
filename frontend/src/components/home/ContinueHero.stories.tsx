import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { continueReading, jumpBackIn, progressSummary } from '../../../.storybook/mocks/home'
import { heatmapDays } from '../../../.storybook/mocks/insights/stats'
import { ContinueHero } from './ContinueHero'
import { ReadingWeeks } from './ReadingWeeks'

const meta = {
  component: ContinueHero,
  tags: ['ai-generated'],
  args: { items: continueReading, rail: 'continue' },
  parameters: { layout: 'padded' },
} satisfies Meta<typeof ContinueHero>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { level: 3, name: 'Lanterns Over Kasumi' })).toBeVisible()
    await waitFor(() => expect(canvas.getByRole('progressbar', { name: 'Reading progress' })).toHaveAttribute('aria-valuenow', '12'))
  },
}

export const NextUpStrip: Story = {
  play: async ({ canvas, canvasElement }) => {
    const heading = await canvas.findByRole('heading', { level: 2, name: 'Next up' })
    await expect(heading).toBeVisible()
    // Joined to the hero as its lower deck, not a rail of its own after it.
    await expect(canvasElement.querySelector('.continue-lead > .continue-tray')).toContainElement(heading)
    // The lead is the hero, so the strip holds everyone else and each poster opens the reader.
    await expect(canvas.queryByRole('link', { name: /Ashen Crown Regression/ })).not.toBeNull()
    await expect(canvas.getAllByRole('link', { name: /Lanterns Over Kasumi/ }).length).toBeGreaterThan(0)
  },
}

export const SingleSeries: Story = {
  args: { items: continueReading.slice(0, 1) },
  play: async ({ canvas }) => {
    await expect(canvas.queryByRole('heading', { name: 'Next up' })).toBeNull()
  },
}

export const NotStartedChapter: Story = {
  args: { items: jumpBackIn, rail: 'jumpback' },
  play: async ({ canvas }) => {
    await expect((await canvas.findAllByRole('link', { name: 'Start chapter' }))[0]).toBeVisible()
  },
}

export const WithoutPageCount: Story = {
  args: { items: [continueReading[2], ...continueReading.slice(0, 2)] },
  play: async ({ canvas }) => {
    await expect(canvas.queryByRole('progressbar')).toBeNull()
  },
}

export const WithoutCover: Story = {
  args: { items: [{ ...jumpBackIn[2], page: 4 }, ...continueReading.slice(0, 3)] },
}

export const ManyInNextUp: Story = {
  args: { items: [...continueReading, ...jumpBackIn] },
}

export const MenuInHero: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'More options for Lanterns Over Kasumi' }))
    const body = within(canvasElement.ownerDocument.body)
    await waitFor(() => expect(body.getByRole('menuitem', { name: 'Open series' })).toBeVisible())
  },
}

/** The hybrid: the glance in the hero's right column and a Next up list in a card beside the hero, no tray. */
export const QueueBeside: Story = {
  args: { items: [...continueReading, ...jumpBackIn], queue: 'beside' },
  render: (args) => <ContinueHero {...args} glance={<ReadingWeeks days={heatmapDays} summary={progressSummary} hosted />} />,
  play: async ({ canvas, canvasElement }) => {
    const list = await canvas.findByRole('region', { name: 'Next up' })
    const glance = await canvas.findByRole('region', { name: 'Last 5 weeks' })
    await expect(canvasElement.querySelector('.continue-hero')).toContainElement(glance)
    // Beside the hero, not inside it.
    await expect(canvasElement.querySelector('.continue-hero')).not.toContainElement(list)
    await expect(canvasElement.querySelector('.continue-lead')).toContainElement(list)
    await expect(canvasElement.querySelector('.continue-tray')).toBeNull()
    await expect(within(list).getByRole('link', { name: /Ashen Crown Regression/ })).toBeVisible()
  },
}

export const QueueBesideWithoutGlance: Story = {
  args: { queue: 'beside' },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('region', { name: 'Next up' })).toBeVisible()
  },
}
