import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { activityFor } from '../../../.storybook/mocks/insights'
import { RewindIntro } from './RewindIntro'

const meta = {
  component: RewindIntro,
  tags: ['ai-generated'],
  parameters: { layout: 'fullscreen' },
  args: {
    stats: activityFor('2026-01-01', '2026-12-31', 'rich'),
    label: '2026',
    onClose: fn(),
  },
} satisfies Meta<typeof RewindIntro>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvasElement }) => {
    const body = within(canvasElement.ownerDocument.body)
    await expect(await body.findByRole('dialog', { name: '2026 Rewind' })).toBeVisible()
    await expect(await body.findByText('Maki Rewind')).toBeVisible()
  },
}

export const Pausing: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    const pause = await body.findByRole('button', { name: 'Pause Rewind' })
    await userEvent.click(pause)
    await waitFor(() => expect(body.getByRole('button', { name: 'Resume Rewind' })).toHaveAttribute('aria-pressed', 'true'))
  },
}

export const ArrowKeysMoveBetweenSlides: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    await body.findByText('Maki Rewind')
    await userEvent.keyboard('{ArrowRight}')
    await waitFor(() => expect(body.getByText('You turned a lot of pages')).toBeVisible(), { timeout: 4000 })
    await userEvent.keyboard('{ArrowLeft}')
    await waitFor(() => expect(body.getByText('Maki Rewind')).toBeVisible(), { timeout: 4000 })
  },
}

export const EscapeCloses: Story = {
  play: async ({ canvasElement, userEvent, args }) => {
    const body = within(canvasElement.ownerDocument.body)
    await body.findByRole('dialog')
    await userEvent.keyboard('{Escape}')
    await waitFor(() => expect(args.onClose).toHaveBeenCalled())
  },
}

export const CloseButton: Story = {
  play: async ({ canvasElement, userEvent, args }) => {
    const body = within(canvasElement.ownerDocument.body)
    await userEvent.click(await body.findByRole('button', { name: 'Close Rewind' }))
    await expect(args.onClose).toHaveBeenCalledOnce()
  },
}

/** A year with no timed reading, no growth and nothing finished is just the title and the summary. */
export const QuietYear: Story = {
  args: { stats: activityFor('2026-01-01', '2026-12-31', 'untracked') },
  play: async ({ canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    await body.findByText('Maki Rewind')
    await userEvent.keyboard('{ArrowRight}')
    await waitFor(() => expect(body.getByText('That was your 2026')).toBeVisible(), { timeout: 4000 })
    // The last slide has nothing left to pause.
    await expect(body.queryByRole('button', { name: 'Pause Rewind' })).toBeNull()
  },
}
