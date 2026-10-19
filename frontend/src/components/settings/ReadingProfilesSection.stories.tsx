import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { settingsMsw } from '../../../.storybook/mocks/settings'
import { api, failWith, neverResolve } from '../../../.storybook/mocks/api'
import { ReadingProfilesSection } from './ReadingProfilesSection'

const meta = {
  component: ReadingProfilesSection,
  tags: ['ai-generated'],
  parameters: { layout: 'padded', msw: settingsMsw() },
  decorators: [
    (Story) => (
      <div style={{ maxWidth: 820 }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof ReadingProfilesSection>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Manga, right to left')).toBeVisible()
    await expect(canvas.getByText('Webtoon scroll')).toBeVisible()
    await expect(canvas.getByText('Night reading')).toBeVisible()
    await expect(canvas.getByText('Everything else')).toBeVisible()
  },
}

export const EditAProfile: Story = {
  play: async ({ canvas, userEvent }) => {
    // The first row is the fixed default, which has no name to edit.
    await canvas.findByText('Manga, right to left')
    const [, edit] = canvas.getAllByRole('button', { name: 'Edit profile' })
    await userEvent.click(edit)
    await expect(await canvas.findByRole('textbox', { name: 'Name' })).toHaveValue('Manga, right to left')
    await expect(canvas.getByRole('button', { name: 'Cancel' })).toBeVisible()
  },
}

export const NewProfile: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'New profile' }))
    const name = await canvas.findByRole('textbox', { name: 'Name' })
    await expect(name).toHaveValue('')
    await userEvent.type(name, 'Tablet landscape')
    await expect(name).toHaveValue('Tablet landscape')
  },
}

export const DeleteAProfile: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await canvas.findByText('Manga, right to left')
    const [remove] = canvas.getAllByRole('button', { name: 'Delete profile' })
    await userEvent.click(remove)
    const dialog = await within(canvasElement.ownerDocument.body).findByRole('dialog')
    await waitFor(() => expect(dialog).toBeVisible())
    await expect(within(dialog).getByText("The profile and its reader settings are removed. This can't be undone.")).toBeVisible()
  },
}

export const NoProfiles: Story = {
  parameters: { msw: settingsMsw(http.get(api('/readingprofiles'), () => HttpResponse.json([]))) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('button', { name: 'New profile' })).toBeVisible()
    await expect(canvas.getAllByRole('button', { name: 'Edit profile' })).toHaveLength(1)
  },
}

export const Loading: Story = {
  parameters: { msw: settingsMsw(http.get(api('/readingprofiles'), () => neverResolve())) },
}

export const LoadFailed: Story = {
  parameters: { msw: settingsMsw(http.get(api('/readingprofiles'), () => failWith(500))) },
}
