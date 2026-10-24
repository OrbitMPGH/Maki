import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor } from 'storybook/test'
import { MonoValue, splitForTruncation } from './MonoValue'

const meta = {
  component: MonoValue,
  tags: ['ai-generated'],
  decorators: [
    (Story) => (
      <div style={{ width: 240 }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof MonoValue>

export default meta
type Story = StoryObj<typeof meta>

const FILE = 'Lanterns Over Kasumi Vol.05 Ch.048 (Scanlator Group).cbz'

export const FileName: Story = {
  args: { value: FILE, full: `Lanterns Over Kasumi/${FILE}`, copy: true },
  play: async ({ canvas }) => {
    const [head, tail] = splitForTruncation(FILE)
    await expect(head + tail).toBe(FILE)
    await expect(tail).toBe('Ch.048 (Scanlator Group).cbz')
    const tailEl = await canvas.findByText(tail)
    await waitFor(() => expect(tailEl).toHaveClass('mono-value-tail'))
    await expect(await canvas.findByRole('button', { name: `Copy Lanterns Over Kasumi/${FILE}` })).toBeInTheDocument()
  },
}

export const FolderPath: Story = {
  args: { value: 'X:\\library\\manga\\Lanterns Over Kasumi', copy: true, c: 'var(--ink-3)' },
}

export const SourceIdLink: Story = {
  args: { value: 'cddd1849-ab34-41f2-9a0e-5d1f0c2b7e61', href: 'https://example.test/series/cddd1849' },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('link')).toHaveAttribute('href', 'https://example.test/series/cddd1849')
  },
}

export const ShortValue: Story = {
  args: { value: '12345' },
  play: async ({ canvas }) => {
    const head = await canvas.findByText('12345')
    await waitFor(() => expect(head).toHaveClass('mono-value-head'))
  },
}
