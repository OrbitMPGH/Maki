import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { continueReading, jumpBackIn } from '../../../.storybook/mocks/home'
import { ContinueHero } from './ContinueHero'

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

export const PickFromNextUp: Story = {
  play: async ({ canvas, userEvent }) => {
    const row = await canvas.findByRole('button', { name: /Ashen Crown Regression/ })
    await waitFor(() => expect(row).toHaveAttribute('aria-pressed', 'false'))
    await userEvent.click(row)
    await waitFor(() => expect(row).toHaveAttribute('aria-pressed', 'true'))
    await expect(await canvas.findByRole('heading', { level: 3, name: 'Ashen Crown Regression' })).toBeVisible()
  },
}

export const SingleSeries: Story = {
  args: { items: continueReading.slice(0, 1) },
  play: async ({ canvas }) => {
    await expect(canvas.queryByRole('complementary', { name: 'Next up' })).toBeNull()
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
