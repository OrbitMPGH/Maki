import type { Meta, StoryObj } from '@storybook/react-vite'
import { Button, Switch, Text } from '@mantine/core'
import { expect, fn } from 'storybook/test'
import { SettingsSection } from './SettingsSection'

const meta = {
  component: SettingsSection,
  tags: ['ai-generated'],
  parameters: { layout: 'padded' },
  args: {
    id: 'example',
    title: 'Download queue',
    description: 'How many chapters download at once and what happens when one fails.',
    children: <Switch label="Retry failed downloads" defaultChecked />,
  },
  decorators: [
    (Story) => (
      <div style={{ maxWidth: 760 }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof SettingsSection>

export default meta
type Story = StoryObj<typeof meta>

export const Clean: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { name: 'Download queue' })).toBeVisible()
    await expect(canvas.queryByText('Unsaved')).toBeNull()
  },
}

export const WithHeadingActions: Story = {
  args: {
    actions: (
      <Button size="xs" variant="default">
        New profile
      </Button>
    ),
  },
}

export const Dirty: Story = {
  args: { dirty: true, onSave: fn(), onDiscard: fn() },
  play: async ({ args, canvas, userEvent }) => {
    await expect(await canvas.findByText('Unsaved')).toBeVisible()
    await userEvent.click(canvas.getByRole('button', { name: 'Save' }))
    await expect(args.onSave).toHaveBeenCalledOnce()
    await userEvent.click(canvas.getByRole('button', { name: 'Discard' }))
    await expect(args.onDiscard).toHaveBeenCalledOnce()
  },
}

export const Saving: Story = {
  args: { dirty: true, saving: true, onSave: fn(), onDiscard: fn() },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('button', { name: 'Discard' })).toBeDisabled()
  },
}

export const SaveDisabled: Story = {
  args: { dirty: true, saveDisabled: true, onSave: fn() },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('button', { name: 'Save' })).toBeDisabled()
  },
}

export const DirtyWithoutSaveHandlerShowsNothing: Story = {
  args: { dirty: true },
  play: async ({ canvas }) => {
    await canvas.findByRole('heading', { name: 'Download queue' })
    await expect(canvas.queryByText('Unsaved')).toBeNull()
  },
}

export const LongDescription: Story = {
  args: {
    description: (
      <Text span>
        A zip of the database and the config file: every series record, reading history and setting, but not the manga
        files, the metadata copy or covers. One is taken automatically before an upgrade migration. Scheduled backups
        are off unless you turn them on below, so take one yourself before big changes.
      </Text>
    ),
  },
}
