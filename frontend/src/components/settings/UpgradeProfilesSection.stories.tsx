import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { settingsMsw } from '../../../.storybook/mocks/settings'
import { api, failWith } from '../../../.storybook/mocks/api'
import { qualityFormats, upgradeProfiles } from '../../../.storybook/mocks/settings/data'
import { QualityFormatsSection, UpgradeProfilesSection } from './UpgradeProfilesSection'

const meta = {
  component: UpgradeProfilesSection,
  tags: ['ai-generated'],
  parameters: { layout: 'padded', msw: settingsMsw() },
  decorators: [
    (Story) => (
      <div style={{ maxWidth: 900 }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof UpgradeProfilesSection>

export default meta
type Story = StoryObj<typeof meta>

async function dialogOf(canvasElement: HTMLElement) {
  const dialog = await within(canvasElement.ownerDocument.body).findByRole('dialog')
  await waitFor(() => expect(dialog).toBeVisible())
  return within(dialog)
}

export const Profiles: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Balanced')).toBeVisible()
    await expect(await canvas.findByText('Storage saver')).toBeVisible()
    await expect(await canvas.findByText('Never upgrades')).toBeVisible()
    await expect(await canvas.findByText('Prefers official releases but accepts good scanlations.')).toBeVisible()
  },
}

export const NoProfilesYet: Story = {
  parameters: { msw: settingsMsw(http.get(api('/upgrade-profiles'), () => HttpResponse.json([]))) },
  play: async ({ canvas }) => {
    await expect(
      await canvas.findByText('No quality profiles yet. Series without one use whatever the instance default is set to.'),
    ).toBeVisible()
  },
}

export const ProfileInUseCannotBeDeleted: Story = {
  play: async ({ canvas }) => {
    await canvas.findByText('Balanced')
    const buttons = await canvas.findAllByRole('button', { name: 'Delete profile' })
    await waitFor(() => expect(buttons[0]).toBeDisabled())
    await waitFor(() => expect(buttons[1]).toBeEnabled())
  },
}

export const UntilScoreProfile: Story = {
  parameters: {
    msw: settingsMsw(
      http.get(api('/upgrade-profiles'), () => HttpResponse.json([{ ...upgradeProfiles[0], upgradeUntilScore: 150 }])),
    ),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Keeps taking better scoring copies until one scores 150')).toBeVisible()
  },
}

export const EditAProfile: Story = {
  play: async ({ canvas, userEvent }) => {
    await canvas.findByText('Balanced')
    await userEvent.click((await canvas.findAllByRole('button', { name: 'Edit profile' }))[0])
    await waitFor(() => expect(canvas.getByRole('textbox', { name: 'Name' })).toHaveValue('Balanced'))
    await expect(await canvas.findByText('Tier order')).toBeVisible()
    await waitFor(() => expect(canvas.getByRole('textbox', { name: 'Score for Official digital' })).toHaveValue('40'))
  },
}

export const EditMoveATier: Story = {
  play: async ({ canvas, userEvent }) => {
    await canvas.findByText('Balanced')
    await userEvent.click((await canvas.findAllByRole('button', { name: 'Edit profile' }))[0])
    await userEvent.click(await canvas.findByRole('button', { name: 'Move Official down' }))
    await waitFor(() => expect(canvas.getAllByRole('button', { name: 'Save' })[0]).toBeEnabled())
  },
}

export const NewProfile: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'New profile' }))
    const name = await canvas.findByRole('textbox', { name: 'Name' })
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Create' })).toBeDisabled())
    await userEvent.type(name, 'Volumes only')
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Create' })).toBeEnabled())
    await userEvent.click(await canvas.findByRole('button', { name: 'Create' }))
    await expect(await within(document.body).findByText('Profile created')).toBeVisible()
  },
}

export const NewProfileRejected: Story = {
  parameters: {
    msw: settingsMsw(http.post(api('/upgrade-profiles'), () => failWith(409, 'A profile with that name already exists.'))),
  },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'New profile' }))
    await userEvent.type(await canvas.findByRole('textbox', { name: 'Name' }), 'Balanced')
    await userEvent.click(await canvas.findByRole('button', { name: 'Create' }))
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Create' })).toBeEnabled())
    await expect(within(document.body).queryByText('Profile created')).toBeNull()
  },
}

export const DeleteUnusedProfile: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await canvas.findByText('Storage saver')
    await userEvent.click((await canvas.findAllByRole('button', { name: 'Delete profile' }))[1])
    const dialog = await dialogOf(canvasElement)
    await expect(await dialog.findByText('Delete Storage saver?')).toBeVisible()
    await expect(await dialog.findByText("The profile is removed. This can't be undone.")).toBeVisible()
  },
}

export const Formats: Story = {
  render: () => <QualityFormatsSection />,
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Official digital')).toBeVisible()
    await expect(await canvas.findByText('Lossless scans')).toBeVisible()
    await expect(await canvas.findByText('Tiny files')).toBeVisible()
  },
}

export const FormatsEmpty: Story = {
  render: () => <QualityFormatsSection />,
  parameters: { msw: settingsMsw(http.get(api('/quality-formats'), () => HttpResponse.json([]))) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('No quality formats yet.')).toBeVisible()
  },
}

export const EditFormat: Story = {
  render: () => <QualityFormatsSection />,
  play: async ({ canvas, userEvent }) => {
    await canvas.findByText('Official digital')
    await userEvent.click((await canvas.findAllByRole('button', { name: 'Edit format' }))[0])
    await waitFor(() => expect(canvas.getByRole('textbox', { name: 'Name' })).toHaveValue('Official digital'))
    await waitFor(() => expect(canvas.getAllByRole('button', { name: 'Remove condition' })).toHaveLength(qualityFormats[0].conditions.length))
    await expect(await canvas.findByRole('button', { name: 'Add condition' })).toBeVisible()
  },
}

export const NewFormat: Story = {
  render: () => <QualityFormatsSection />,
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'New format' }))
    await userEvent.type(await canvas.findByRole('textbox', { name: 'Name' }), 'Wide pages')
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Create' })).toBeEnabled())
  },
}

export const DeleteFormatConfirm: Story = {
  render: () => <QualityFormatsSection />,
  play: async ({ canvas, canvasElement, userEvent }) => {
    await canvas.findByText('Tiny files')
    await userEvent.click((await canvas.findAllByRole('button', { name: 'Delete format' }))[2])
    const dialog = await dialogOf(canvasElement)
    await expect(await dialog.findByText('Delete Tiny files?')).toBeVisible()
    await expect(await dialog.findByText("Removed from every profile that scores it. This can't be undone.")).toBeVisible()
  },
}
