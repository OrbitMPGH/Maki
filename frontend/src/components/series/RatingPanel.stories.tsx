import { useState } from 'react'
import type { Meta, StoryObj } from '@storybook/react-vite'
import { http, HttpResponse } from 'msw'
import { expect, fn, waitFor, within } from 'storybook/test'
import { api } from '../../../.storybook/mocks/api'
import { ratingSettings } from '../../../.storybook/mocks/shared'
import { RatingPanel } from './RatingPanel'

const decimal = [
  http.get(api('/settings/ratings'), () => HttpResponse.json({ ...ratingSettings, precision: 'decimal' })),
]

const meta = {
  component: RatingPanel,
  tags: ['ai-generated'],
  args: {
    seriesId: 1,
    value: 8,
    finished: true,
    breakdown: { story: 8, characters: 9, art: 6 },
    onChange: fn(),
    onClear: fn(),
  },
} satisfies Meta<typeof RatingPanel>

export default meta
type Story = StoryObj<typeof meta>

/** Renders the panel with a button that unmounts it, the way closing the popover does. */
function Closable(args: Parameters<typeof RatingPanel>[0]) {
  const [open, setOpen] = useState(true)
  return (
    <>
      <button type="button" onClick={() => setOpen(false)}>
        Close panel
      </button>
      {open && <RatingPanel {...args} />}
    </>
  )
}

export const PickAWordSavesOnClose: Story = {
  args: { value: 7 },
  render: Closable,
  play: async ({ args, canvas, userEvent }) => {
    const words = await canvas.findByRole('group', { name: 'Your rating' })
    await expect(within(words).getByRole('button', { name: 'Good', pressed: true })).toBeVisible()
    await userEvent.click(within(words).getByRole('button', { name: 'Excellent' }))
    await waitFor(() => expect(within(words).getByRole('button', { name: 'Excellent' })).toHaveAttribute('aria-pressed', 'true'))
    await expect(args.onChange).not.toHaveBeenCalled()
    await userEvent.click(canvas.getByRole('button', { name: 'Close panel' }))
    await waitFor(() => {
      expect(args.onChange).toHaveBeenCalledOnce()
      expect(args.onChange).toHaveBeenCalledWith(9)
    })
  },
}

export const TheCurrentWordKeepsTheDecimal: Story = {
  args: { value: 7.8 },
  parameters: { msw: decimal },
  render: Closable,
  play: async ({ args, canvas, userEvent }) => {
    const words = await canvas.findByRole('group', { name: 'Your rating' })
    await userEvent.click(within(words).getByRole('button', { name: 'Very good', pressed: true }))
    await userEvent.click(canvas.getByRole('button', { name: 'Close panel' }))
    await expect(args.onChange).not.toHaveBeenCalled()
  },
}

export const NudgeSavesOnClose: Story = {
  args: { value: 7.8 },
  parameters: { msw: decimal },
  render: Closable,
  play: async ({ args, canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Raise by 0.1' }))
    await userEvent.click(canvas.getByRole('button', { name: 'Raise by 0.1' }))
    await expect(await canvas.findByRole('button', { name: 'Type a rating' })).toHaveTextContent('8.0')
    await expect(args.onChange).not.toHaveBeenCalled()
    await userEvent.click(canvas.getByRole('button', { name: 'Close panel' }))
    await waitFor(() => {
      expect(args.onChange).toHaveBeenCalledOnce()
      expect(args.onChange).toHaveBeenCalledWith(8)
    })
  },
}

export const BreakdownOpensTheNextUnscored: Story = {
  args: { value: 7.8 },
  parameters: { msw: decimal },
  play: async ({ args, canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: /Break it down/ }))
    const enjoyment = await canvas.findByRole('group', { name: 'Enjoyment' })
    await userEvent.click(within(enjoyment).getByRole('button', { name: 'Good' }))
    await waitFor(() => expect(canvas.queryByRole('group', { name: 'Enjoyment' })).toBeNull())
    await waitFor(() => expect(canvas.getByText('Very good 7.5')).toBeVisible())
    await userEvent.click(canvas.getByRole('button', { name: 'Use' }))
    await waitFor(() => expect(args.onChange).toHaveBeenCalledWith(7.5))
  },
}

export const ScoreEveryCategoryInARow: Story = {
  args: { value: null, breakdown: null },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: /Break it down/ }))
    for (const [category, word] of [
      ['Story', 'Very good'],
      ['Characters', 'Excellent'],
      ['Art', 'Decent'],
      ['Enjoyment', 'Good'],
    ]) {
      const group = await canvas.findByRole('group', { name: category })
      await userEvent.click(within(group).getByRole('button', { name: word }))
    }
    await waitFor(() => expect(canvas.getByText('Very good 7.5')).toBeVisible())
  },
}

export const WholeNumbersUseRoundsTheSuggestion: Story = {
  args: { value: 7 },
  play: async ({ args, canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: /Break it down/ }))
    await userEvent.click(await canvas.findByRole('button', { name: 'Use' }))
    await waitFor(() => expect(args.onChange).toHaveBeenCalledWith(8))
  },
}

export const Matches: Story = {
  args: { value: 7.8 },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: /Break it down/ }))
    await waitFor(() => expect(canvas.getByText('Matches your rating')).toBeVisible())
  },
}

export const ClearRating: Story = {
  play: async ({ args, canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Clear rating' }))
    await waitFor(() => expect(args.onClear).toHaveBeenCalledOnce())
    await expect(await canvas.findByText('Not rated')).toBeVisible()
  },
}

export const EditCategoriesInThePanel: Story = {
  args: { canEditCategories: true },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: /Break it down/ }))
    await userEvent.click(await canvas.findByRole('button', { name: /Edit categories/ }))
    await waitFor(() => expect(canvas.getByRole('textbox', { name: 'New category' })).toBeVisible())
  },
}

export const SoFar: Story = {
  args: { value: 7, soFar: true, finished: false, breakdown: { story: 7, characters: 8 } },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('/10 so far')).toBeVisible()
  },
}

export const WithoutBreakdown: Story = {
  args: { breakdown: undefined },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('group', { name: 'Your rating' })).toBeVisible()
    await expect(canvas.queryByRole('button', { name: /Break it down/ })).toBeNull()
  },
}
