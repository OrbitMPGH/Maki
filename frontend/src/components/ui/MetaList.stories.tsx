import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor } from 'storybook/test'
import { MetaList } from './MetaList'

const meta = {
  component: MetaList,
  tags: ['ai-generated'],
  args: { items: ['Ongoing', '2019', 'Manhwa', '142 chapters'], style: { color: 'var(--ink-3)', fontSize: 'var(--type-meta)' } },
} satisfies Meta<typeof MetaList>

export default meta
type Story = StoryObj<typeof meta>

export const Facts: Story = {
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getAllByRole('listitem')).toHaveLength(4))
  },
}

/** Titles can hold commas, which is why the separator is a hairline and not a comma. */
export const TitlesWithCommas: Story = {
  args: { items: ['Hikari no Tou', '光の塔', 'Rise, Tower of Light'] },
}

/** Inline spans, for a parent that truncates or line-clamps. */
export const Inline: Story = {
  args: { inline: true, items: ['Chapter 12', 'MangaDex'] },
}

export const SkipsEmptyItems: Story = {
  args: { items: ['Syncing now', null, false, '', 'Next in 26 min'] },
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getAllByRole('listitem')).toHaveLength(2))
  },
}
