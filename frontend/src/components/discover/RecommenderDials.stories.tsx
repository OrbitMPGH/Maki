import { useState } from 'react'
import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { SimpleGrid } from '@mantine/core'
import { RecommenderDials } from './RecommenderDials'

const meta = {
  component: RecommenderDials,
  tags: ['ai-generated'],
  args: { obscurity: 0, diversity: 0, setObscurity: () => {}, setDiversity: () => {} },
  render: function Render(args) {
    const [obscurity, setObscurity] = useState(args.obscurity)
    const [diversity, setDiversity] = useState(args.diversity)
    return (
      <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="lg" style={{ maxWidth: 720 }}>
        <RecommenderDials
          obscurity={obscurity}
          setObscurity={setObscurity}
          diversity={diversity}
          setDiversity={setDiversity}
        />
      </SimpleGrid>
    )
  },
  parameters: { layout: 'padded' },
} satisfies Meta<typeof RecommenderDials>

export default meta
type Story = StoryObj<typeof meta>

export const Balanced: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Obscurity: balanced')).toBeVisible()
    await expect(await canvas.findByText('Variety: closest matches')).toBeVisible()
  },
}

export const HiddenGemsAndVaried: Story = {
  args: { obscurity: 0.75, diversity: 0.6 },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Obscurity: hidden gems (+0.75)')).toBeVisible()
    await expect(await canvas.findByText('Variety: spread out (0.60)')).toBeVisible()
  },
}

export const Mainstream: Story = {
  args: { obscurity: -0.5 },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Obscurity: mainstream (-0.50)')).toBeVisible()
  },
}

export const MoveTheDials: Story = {
  play: async ({ canvas, userEvent }) => {
    const [obscurity] = await canvas.findAllByRole('slider')
    obscurity.focus()
    await userEvent.keyboard('{ArrowRight}{ArrowRight}')
    await expect(await canvas.findByText('Obscurity: hidden gems (+0.50)')).toBeVisible()
  },
}
