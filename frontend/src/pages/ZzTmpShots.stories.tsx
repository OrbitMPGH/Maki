import type { Meta, StoryObj } from '@storybook/react-vite'
import { page } from 'vitest/browser'
import { waitFor, within, expect } from 'storybook/test'
import SeriesDetailPage from './SeriesDetailPage'
import { seriesMsw } from '../../.storybook/mocks/series'
import { FinishedRateHint } from './SeriesDetailPage.stories'

const meta = {
  title: 'Tmp/Shots',
  component: SeriesDetailPage,
  parameters: { layout: 'fullscreen', msw: seriesMsw() },
} satisfies Meta<typeof SeriesDetailPage>
export default meta
type Story = StoryObj<typeof meta>

const shot = async (name: string) => {
  await new Promise((r) => setTimeout(r, 600))
  await page.screenshot({ path: `C:/Users/Orbit/Documents/GitHub/Maki/.shots-tmp/${name}.png` })
}

export const Shots: Story = {
  beforeEach: FinishedRateHint.beforeEach,
  parameters: FinishedRateHint.parameters,
  play: async ({ canvas, userEvent }) => {
    await expect(await canvas.findByText("You've finished this. How was it?")).toBeVisible()
    await shot('1-rate-hint')
    const stars = canvas.getByRole('group', { name: 'Rate this series' })
    await userEvent.hover((within(stars).getByRole('radio', { name: '4' }) as HTMLInputElement).labels![0])
    await shot('2-hover')
    await userEvent.click((within(stars).getByRole('radio', { name: '4' }) as HTMLInputElement).labels![0])
    await waitFor(() => expect(canvas.getByText("You've finished this. Archive it?")).toBeVisible())
    await shot('3-after-rating')
  },
}
