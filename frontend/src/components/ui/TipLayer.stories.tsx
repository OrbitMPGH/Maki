import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { TipLayer } from './TipLayer'

const meta = {
  component: TipLayer,
  tags: ['ai-generated'],
  parameters: { layout: 'padded' },
  render: () => (
    <>
      <TipLayer />
      <div style={{ display: 'flex', gap: 16, paddingTop: 80 }}>
        <button type="button" data-tip="Mark as read">
          Mark
        </button>
        <button type="button" data-tip="Chapter 142 of 143, downloaded from MangaDex">
          Chapter 142
        </button>
        <button type="button">No tip</button>
      </div>
    </>
  ),
} satisfies Meta<typeof TipLayer>

export default meta
type Story = StoryObj<typeof meta>

export const OnHover: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    // The bubble lives in a portal on document.body, rendered empty and hidden until a target is hovered.
    const body = within(canvasElement.ownerDocument.body)
    const tip = await body.findByRole('tooltip', { hidden: true })
    await waitFor(() => expect(tip).toHaveAttribute('aria-hidden', 'true'))
    await userEvent.hover(await canvas.findByRole('button', { name: 'Mark' }))
    await waitFor(() => expect(tip).toHaveAttribute('data-open'))
    await waitFor(() => expect(tip).toHaveTextContent('Mark as read'))
    await userEvent.unhover(await canvas.findByRole('button', { name: 'Mark' }))
    await userEvent.hover(await canvas.findByRole('button', { name: 'No tip' }))
    await waitFor(() => expect(tip).not.toHaveAttribute('data-open'))
  },
}

export const OnKeyboardFocus: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    const tip = await within(canvasElement.ownerDocument.body).findByRole('tooltip', { hidden: true })
    await userEvent.tab()
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Mark' })).toHaveFocus())
    await waitFor(() => expect(tip).toHaveAttribute('data-open'))
    await userEvent.tab()
    await waitFor(() => expect(tip).toHaveTextContent('Chapter 142 of 143, downloaded from MangaDex'))
  },
}
