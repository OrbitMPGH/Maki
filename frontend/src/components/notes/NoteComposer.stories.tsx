import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor } from 'storybook/test'
import NoteComposer from './NoteComposer'

const meta = {
  component: NoteComposer,
  tags: ['ai-generated'],
  args: { onSubmit: fn(), singleUser: false },
  decorators: [
    (Story) => (
      <div style={{ maxWidth: 420, padding: 16 }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof NoteComposer>

export default meta
type Story = StoryObj<typeof meta>

export const NewNote: Story = {
  args: { pinPage: 5 },
  play: async ({ args, canvas, userEvent }) => {
    const add = await canvas.findByRole('button', { name: 'Add note' })
    await expect(add).toBeDisabled()
    await userEvent.type(await canvas.findByRole('textbox', { name: 'Write a note' }), '  A thought  ')
    await expect(await canvas.findByRole('checkbox', { name: 'Pin to page 6' })).toBeChecked()
    await expect(await canvas.findByText('Only you')).toBeVisible()
    await waitFor(() => expect(add).toBeEnabled())
    await userEvent.click(add)
    await waitFor(() => expect(args.onSubmit).toHaveBeenCalledWith({ body: 'A thought', pageIndex: 5, visibility: 'private' }))
    await waitFor(() => expect(canvas.getByRole('textbox', { name: 'Write a note' })).toHaveValue(''))
  },
}

export const SharedWithoutDisplayName: Story = {
  args: { hasDisplayName: false },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByText('Shared'))
    await expect(await canvas.findByText('Everyone who can see this series')).toBeVisible()
    await expect(await canvas.findByText(/Others will see you as "Another reader"/)).toBeVisible()
  },
}

export const SharedUnderLoginName: Story = {
  args: { hasDisplayName: false, loginName: 'ana' },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByText('Shared'))
    await expect(await canvas.findByText('Others will see your login name, ana.')).toBeVisible()
  },
}

export const SharedOnIncognitoSeries: Story = {
  args: { incognito: true },
  play: async ({ canvas, userEvent }) => {
    await expect(canvas.queryByText(/This series is incognito/)).toBeNull()
    await userEvent.click(await canvas.findByText('Shared'))
    await expect(await canvas.findByText(/This series is incognito/)).toBeVisible()
  },
}

export const SingleUser: Story = {
  args: { singleUser: true },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('textbox', { name: 'Write a note' })).toBeVisible()
    await expect(canvas.queryByText('Only you')).toBeNull()
  },
}

export const Reply: Story = {
  args: { variant: 'reply' },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('button', { name: 'Reply' })).toBeDisabled()
    await expect(canvas.queryByRole('checkbox')).toBeNull()
  },
}

export const Editing: Story = {
  args: {
    initial: { body: 'Old text', pageIndex: 11, visibility: 'shared' },
    submitLabel: 'Save',
    onCancel: fn(),
  },
  play: async ({ args, canvas, userEvent }) => {
    await expect(await canvas.findByRole('textbox', { name: 'Write a note' })).toHaveValue('Old text')
    await userEvent.click(await canvas.findByRole('checkbox', { name: 'Pin to page 12' }))
    await userEvent.click(await canvas.findByRole('button', { name: 'Save' }))
    await waitFor(() => expect(args.onSubmit).toHaveBeenCalledWith({ body: 'Old text', pageIndex: null, visibility: 'shared' }))
  },
}

export const NearTheLimit: Story = {
  args: { initial: { body: 'x'.repeat(3990), pageIndex: null, visibility: 'private' } },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('3990 / 4000')).toBeVisible()
    await waitFor(() => expect(canvas.getByRole('textbox', { name: 'Write a note' })).toHaveAttribute('maxlength', '4000'))
  },
}

export const Offline: Story = {
  args: { disabled: true },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('textbox', { name: 'Write a note' })).toBeDisabled()
    await expect(await canvas.findByRole('button', { name: 'Add note' })).toBeDisabled()
  },
}

export const WithError: Story = {
  args: { error: 'That note is too long.' },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('That note is too long.')).toBeVisible()
  },
}
