import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor } from 'storybook/test'
import { catalogueItem, discoverPage, resetTabState } from '../../../.storybook/mocks/discover'
import { shortlistEntry, shortlistMock } from '../../../.storybook/mocks/shortlist'
import { ShortlistButton, ShortlistNote } from './ShortlistButton'

const item = catalogueItem(1)
const unsaved = shortlistMock([])
const saved = shortlistMock([
  shortlistEntry(item, { note: 'After the current arc ends.', savedFrom: 'search', createdAtUtc: new Date().toISOString() }),
])

const meta = {
  component: ShortlistButton,
  tags: ['ai-generated'],
  beforeEach: () => {
    resetTabState()
    unsaved.reset()
    saved.reset()
  },
  args: { providerId: item.providerId, title: item.title, source: { savedFrom: 'rail:trending' } },
  parameters: { layout: 'centered', msw: discoverPage(...unsaved.handlers) },
  decorators: [
    (Story, ctx) => (
      <div style={{ width: 220, display: 'grid', gap: 8 }}>
        <Story />
        <ShortlistNote providerId={ctx.args.providerId} />
      </div>
    ),
  ],
} satisfies Meta<typeof ShortlistButton>

export default meta
type Story = StoryObj<typeof meta>

export const NotShortlisted: Story = {
  play: async ({ canvas, userEvent }) => {
    const button = await canvas.findByRole('button', { name: 'Shortlist' })
    await expect(button).toHaveAttribute('aria-pressed', 'false')
    await userEvent.click(button)
    await expect(await canvas.findByRole('button', { name: 'Shortlisted' })).toHaveAttribute('aria-pressed', 'true')
    await waitFor(() => expect(unsaved.snapshot().map((e) => e.savedFrom)).toEqual(['rail:trending']))
  },
}

export const Shortlisted: Story = {
  parameters: { msw: discoverPage(...saved.handlers) },
  play: async ({ canvas, userEvent }) => {
    const button = await canvas.findByRole('button', { name: 'Shortlisted' })
    await expect(button).toHaveAttribute('aria-pressed', 'true')
    const note = await canvas.findByRole('textbox', { name: 'Note for later' })
    await expect(note).toHaveValue('After the current arc ends.')
    await userEvent.clear(note)
    await userEvent.type(note, 'Next month')
    await userEvent.tab()
    await waitFor(() => expect(saved.snapshot()[0].note).toBe('Next month'))
  },
}
