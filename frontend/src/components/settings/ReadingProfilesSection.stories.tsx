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
    await expect(await canvas.findByText('Webtoon scroll')).toBeVisible()
    await expect(await canvas.findByText('Night reading')).toBeVisible()
    await expect(await canvas.findByText('Everything else')).toBeVisible()
  },
}

export const EditAProfile: Story = {
  play: async ({ canvas, userEvent }) => {
    // The first row is the fixed default, which has no name to edit.
    await canvas.findByText('Manga, right to left')
    const [, edit] = await canvas.findAllByRole('button', { name: 'Edit profile' })
    await userEvent.click(edit)
    await waitFor(() => expect(canvas.getByRole('textbox', { name: 'Name' })).toHaveValue('Manga, right to left'))
    await expect(await canvas.findByRole('button', { name: 'Cancel' })).toBeVisible()
  },
}

export const NewProfile: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'New profile' }))
    const name = await canvas.findByRole('textbox', { name: 'Name' })
    await waitFor(() => expect(name).toHaveValue(''))
    await userEvent.type(name, 'Tablet landscape')
    await waitFor(() => expect(name).toHaveValue('Tablet landscape'))
  },
}

export const DeleteAProfile: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await canvas.findByText('Manga, right to left')
    const [remove] = await canvas.findAllByRole('button', { name: 'Delete profile' })
    await userEvent.click(remove)
    const dialog = await within(canvasElement.ownerDocument.body).findByRole('dialog')
    await waitFor(() => expect(dialog).toBeVisible())
    await expect(await within(dialog).findByText("The profile and its reader settings are removed. This can't be undone.")).toBeVisible()
  },
}

export const NoProfiles: Story = {
  parameters: { msw: settingsMsw(http.get(api('/readingprofiles'), () => HttpResponse.json([]))) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('button', { name: 'New profile' })).toBeVisible()
    await waitFor(() => expect(canvas.getAllByRole('button', { name: 'Edit profile' })).toHaveLength(1))
  },
}

export const Loading: Story = {
  parameters: { msw: settingsMsw(http.get(api('/readingprofiles'), () => neverResolve())) },
}

export const LoadFailed: Story = {
  parameters: { msw: settingsMsw(http.get(api('/readingprofiles'), () => failWith(500))) },
}
