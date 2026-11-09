import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor } from 'storybook/test'
import NoteBody from './NoteBody'

const meta = {
  component: NoteBody,
  tags: ['ai-generated'],
  args: { text: 'Plain text.' },
  decorators: [
    (Story) => (
      <div style={{ maxWidth: 420, padding: 16 }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof NoteBody>

export default meta
type Story = StoryObj<typeof meta>

export const PlainText: Story = {
  args: { text: 'The tea cups come back later.\nSecond line keeps its break.' },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/The tea cups come back later/)).toBeVisible()
  },
}

export const Links: Story = {
  args: { text: 'Notes at https://example.com/notes, and also (http://example.org/page).' },
  play: async ({ canvas }) => {
    const first = await canvas.findByRole('link', { name: 'https://example.com/notes' })
    await expect(first).toHaveAttribute('href', 'https://example.com/notes')
    await expect(first).toHaveAttribute('target', '_blank')
    await expect(first).toHaveAttribute('rel', 'noopener noreferrer nofollow')
    await expect(await canvas.findByRole('link', { name: 'http://example.org/page' })).toBeVisible()
  },
}

export const Spoiler: Story = {
  args: { text: 'She wins. ||The scarf was hers all along.|| Wild.' },
  play: async ({ canvas, userEvent }) => {
    const spoiler = await canvas.findByRole('button', { name: 'Spoiler, press to reveal' })
    await userEvent.click(spoiler)
    await waitFor(() => expect(canvas.queryByRole('button', { name: 'Spoiler, press to reveal' })).toBeNull())
    await expect(await canvas.findByText('The scarf was hers all along.')).toBeVisible()
  },
}

export const SpoilerFromKeyboard: Story = {
  args: { text: '||Hidden||' },
  play: async ({ canvas, userEvent }) => {
    await userEvent.tab()
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Spoiler, press to reveal' })).toHaveFocus())
    await userEvent.keyboard('{Enter}')
    await expect(await canvas.findByText('Hidden')).toBeVisible()
  },
}

export const EmptySpoiler: Story = {
  args: { text: 'Before |||| after' },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('button', { name: 'Spoiler, press to reveal' })).toBeVisible()
  },
}

export const MarkupStaysText: Story = {
  args: { text: '<img src=x onerror=alert(1)> <b>bold</b>' },
  play: async ({ canvas, canvasElement }) => {
    await expect(await canvas.findByText(/<b>bold<\/b>/)).toBeVisible()
    await expect(canvasElement.querySelector('img')).toBeNull()
  },
}
