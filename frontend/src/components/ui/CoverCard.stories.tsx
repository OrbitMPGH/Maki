import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor } from 'storybook/test'
import { librarySeries } from '../../../.storybook/mocks/home'
import { CoverCard } from './CoverCard'

const [lanterns, kitchen, ironPetal, , shiokaze, ashen, , bluefin, graveRobber, , neon] = librarySeries

const meta = {
  component: CoverCard,
  tags: ['ai-generated'],
  args: { series: lanterns, selectMode: false, selected: false, readTracking: true, onToggle: fn() },
  parameters: { layout: 'centered' },
  decorators: [
    (Story) => (
      <div style={{ width: 200 }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof CoverCard>

export default meta
type Story = StoryObj<typeof meta>

export const PartlyRead: Story = {
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getByRole('link')).toHaveAttribute('href', '/series/1'))
    await expect(await canvas.findByText('11 new')).toBeVisible()
  },
}

export const FullyRead: Story = {
  args: { series: shiokaze },
  play: async ({ canvas }) => {
    await expect(canvas.queryByText(/new$/)).toBeNull()
    const disc = await canvas.findByRole('img', { name: /\. All downloaded chapters read$/ })
    await waitFor(() => expect(disc).toHaveAttribute('data-read', 'all'))
  },
}

export const Completed: Story = {
  args: { series: kitchen },
}

export const DownloadingAndBehind: Story = {
  args: { series: ironPetal },
  play: async ({ canvas }) => {
    // Compact beside an "N new" pill, the long form otherwise.
    await expect(
      await canvas.findByText((_, el) => el?.tagName === 'SPAN' && /^60( of |\/)88$/.test(el.textContent ?? '')),
    ).toBeVisible()
    // The download takes the left corner; reading progress stays on the status disc.
    await expect(await canvas.findByRole('img', { name: /downloaded read$/ })).toHaveAttribute('data-read', 'part')
  },
}

export const NotMonitored: Story = {
  args: { series: neon },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('img', { name: 'Not monitored' })).toBeInTheDocument()
    await expect(await canvas.findByRole('img', { name: /, not monitored(\.|$)/ })).toHaveAttribute('data-unmonitored')
  },
}

export const Muted: Story = {
  args: { series: { ...ashen, notificationMode: 'Muted' } },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('img', { name: 'Notifications muted' })).toBeInTheDocument()
  },
}

export const ReadTrackingOff: Story = {
  args: { series: lanterns, readTracking: false },
  play: async ({ canvas }) => {
    await expect(canvas.queryByText('11 new')).toBeNull()
  },
}

export const NeverTracked: Story = {
  args: { series: bluefin },
}

export const NoCover: Story = {
  args: { series: { ...graveRobber, coverUrl: null } },
}

export const LongTitle: Story = {
  args: { series: { ...graveRobber, displayTitle: 'Grave Robber of the Seventh Hall: The Complete Chronicle of Everything Below the Capital' } },
}

export const NoChaptersListed: Story = {
  args: {
    series: { ...graveRobber, totalChapters: null, wantedChapterCount: 0, knownChapterCount: 0, chapterFileCount: 0, readChapterCount: null },
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('No chapters')).toBeVisible()
  },
}

export const SelectMode: Story = {
  args: { selectMode: true },
  play: async ({ args, canvas, userEvent }) => {
    const card = await canvas.findByRole('checkbox')
    await expect(card).toHaveAttribute('aria-checked', 'false')
    await userEvent.click(card)
    await waitFor(() => expect(args.onToggle).toHaveBeenCalledWith(1))
    ;(args.onToggle as ReturnType<typeof fn>).mockClear()
    card.focus()
    await userEvent.keyboard(' ')
    await waitFor(() => expect(args.onToggle).toHaveBeenCalledTimes(1))
    await expect(args.onToggle).toHaveBeenCalledWith(1)
  },
}

export const Selected: Story = {
  args: { selectMode: true, selected: true },
}

/** Offline, with chapters saved on this device: a device mark joins the left corner. */
export const SavedOffline: Story = {
  args: { offline: 'saved' },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('img', { name: 'Saved on this device' })).toBeInTheDocument()
  },
}

/** Offline, and this device holds no copy of the series page: dimmed, and the count row says why. */
export const UnavailableOffline: Story = {
  args: { offline: 'unavailable' },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Not available offline')).toBeVisible()
    await expect(canvas.queryByText('11 new')).toBeNull()
    await waitFor(() => expect(canvas.getByRole('link')).toHaveAttribute('data-offline', 'unavailable'))
  },
}
