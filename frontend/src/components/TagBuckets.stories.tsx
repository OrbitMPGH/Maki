import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import type { MangaBakaTag } from '../api/hooks'
import { TagBuckets } from './TagBuckets'

const tag = (name: string, weight: string, extra: Partial<MangaBakaTag> = {}): MangaBakaTag => ({
  name,
  weight,
  description: null,
  isSpoiler: false,
  ...extra,
})

const sample: MangaBakaTag[] = [
  tag('Found Family', 'core', { description: 'Strangers who end up relying on each other like relatives.' }),
  tag('Travel', 'core'),
  tag('Magic', 'defining'),
  tag('Festival', 'defining'),
  tag('Lamplighters', 'defining', { description: 'A character whose job is tending lamps.' }),
  tag('Amnesia', 'recurrent'),
  tag('Seasonal Cycle', 'recurrent'),
  tag('Caravan', 'recurrent'),
  tag('Unreliable Memory', 'incidental', { isSpoiler: true, description: 'The narrator has been wrong about the first winter.' }),
  tag('Tea', 'incidental'),
  tag('Dream Sequence', 'incidental', { isSpoiler: true }),
]

const crowded: MangaBakaTag[] = [
  tag('Core Theme', 'core'),
  ...Array.from({ length: 30 }, (_, i) => tag(`Incidental Detail ${i + 1}`, 'incidental')),
]

const meta = {
  component: TagBuckets,
  tags: ['ai-generated'],
  args: { tags: sample },
  decorators: [
    (Story) => (
      <div style={{ maxWidth: 640 }}>
        <Story />
      </div>
    ),
  ],
  parameters: { layout: 'padded' },
} satisfies Meta<typeof TagBuckets>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(canvas.getByText('Core')).toBeVisible()
    await expect(canvas.getByText('Incidental')).toBeVisible()
    await expect(canvas.getByText('Found Family')).toBeVisible()
  },
}

export const SpoilerTooltip: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.hover(canvas.getByText('Unreliable Memory'))
    const tip = await within(canvasElement.ownerDocument.body).findByText(
      'Spoiler · The narrator has been wrong about the first winter.',
    )
    await waitFor(() => expect(tip).toBeVisible())
  },
}

export const ManyTagsCollapsed: Story = {
  args: { tags: crowded },
  play: async ({ canvas, userEvent }) => {
    await expect(canvas.queryByText('Incidental Detail 30')).toBeNull()
    await userEvent.click(canvas.getByRole('button', { name: '+12 more' }))
    await expect(canvas.getByText('Incidental Detail 30')).toBeVisible()
    await userEvent.click(canvas.getByRole('button', { name: 'Show fewer' }))
    await expect(canvas.queryByText('Incidental Detail 30')).toBeNull()
  },
}

export const UnknownWeight: Story = {
  args: { tags: [...sample.slice(0, 3), tag('Uncategorised Oddity', 'mystery')] },
  play: async ({ canvas }) => {
    await expect(canvas.getByText('Other')).toBeVisible()
  },
}

export const OneBucket: Story = {
  args: { tags: sample.filter((t) => t.weight === 'core') },
}

export const NoTags: Story = {
  args: { tags: [] },
}
