import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api } from '../../../.storybook/mocks/api'
import { pageUrlsFor } from '../../../.storybook/mocks/entry/fixtures'
import { toneFilter } from './prefs'
import PagedView from './PagedView'

const meta = {
  component: PagedView,
  tags: ['ai-generated'],
  parameters: { layout: 'fullscreen' },
  args: {
    urls: pageUrlsFor(1132, 18),
    spread: [5],
    fit: 'height',
    direction: 'rtl',
    zoom: 1,
    scale: 100,
    label: 'Chapter 132',
    onMeasure: fn(),
  },
  decorators: [
    (Story) => (
      <div className="reader-root">
        <div className="reader-surface">
          <Story />
        </div>
      </div>
    ),
  ],
} satisfies Meta<typeof PagedView>

export default meta
type Story = StoryObj<typeof meta>

export const SinglePage: Story = {
  play: async ({ args, canvas }) => {
    await expect(await canvas.findByRole('img', { name: 'Chapter 132 - page 6' })).toBeVisible()
    await waitFor(() => expect(args.onMeasure).toHaveBeenCalledWith(5, expect.anything()))
  },
}

export const FitWidth: Story = {
  args: { fit: 'width' },
}

export const OriginalSizeScaledDown: Story = {
  args: { fit: 'original', scale: 60 },
}

export const DoublePageRightToLeft: Story = {
  args: { spread: [3, 4] },
  play: async ({ canvas, canvasElement }) => {
    await canvas.findByRole('img', { name: 'Chapter 132 - page 4' })
    await expect(await canvas.findByRole('img', { name: 'Chapter 132 - page 5' })).toBeVisible()
    // Right to left puts the lower page number on the right.
    const [first, second] = Array.from(canvasElement.querySelectorAll('img.reader-page')).map((img) => img.getAttribute('alt'))
    await expect(first).toBe('Chapter 132 - page 5')
    await expect(second).toBe('Chapter 132 - page 4')
  },
}

export const DoublePageLeftToRight: Story = {
  args: { spread: [3, 4], direction: 'ltr' },
  play: async ({ canvas, canvasElement }) => {
    await canvas.findByRole('img', { name: 'Chapter 132 - page 4' })
    const [first] = Array.from(canvasElement.querySelectorAll('img.reader-page')).map((img) => img.getAttribute('alt'))
    await expect(first).toBe('Chapter 132 - page 4')
  },
}

export const ZoomedIn: Story = {
  args: { zoom: 2 },
}

export const SepiaAndDimmed: Story = {
  args: { tone: toneFilter({ dim: 30, filter: 'sepia' }) },
}

export const PageFailsToLoad: Story = {
  args: { urls: pageUrlsFor(4404, 10), spread: [0], label: 'Chapter 61' },
  parameters: {
    msw: [http.get(api('/reader/chapter/4404/page/:n'), () => new HttpResponse(null, { status: 500 }))],
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('This page failed to load.')).toBeVisible()
    await expect(await canvas.findByRole('button', { name: 'Try again' })).toBeVisible()
  },
}
