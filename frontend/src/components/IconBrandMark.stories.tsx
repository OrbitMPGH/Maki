import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor } from 'storybook/test'
import { IconBrandMark, type BrandMood } from './IconBrandMark'

const MOODS: BrandMood[] = ['awake', 'asleep', 'asking', 'pleased']

const meta = {
  component: IconBrandMark,
  tags: ['ai-generated'],
  args: { size: 96 },
  argTypes: { mood: { control: 'select', options: MOODS } },
} satisfies Meta<typeof IconBrandMark>

export default meta
type Story = StoryObj<typeof meta>

export const Awake: Story = { args: { mood: 'awake' } }

export const Asleep: Story = { args: { mood: 'asleep' } }

export const Asking: Story = { args: { mood: 'asking' } }

export const Pleased: Story = { args: { mood: 'pleased' } }

export const AllMoods: Story = {
  render: (args) => (
    <div style={{ display: 'flex', gap: 32 }}>
      {MOODS.map((mood) => (
        <figure key={mood} style={{ margin: 0, textAlign: 'center' }} data-mood={mood}>
          <IconBrandMark {...args} mood={mood} />
          <figcaption style={{ marginTop: 8, fontSize: 13 }}>{mood}</figcaption>
        </figure>
      ))}
    </div>
  ),
  play: async ({ canvasElement }) => {
    await waitFor(() => expect(canvasElement.querySelectorAll('svg')).toHaveLength(MOODS.length))
  },
}

export const SidebarSize: Story = { args: { size: 30 } }
