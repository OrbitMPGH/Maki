import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { settingsMsw } from '../../../.storybook/mocks/settings'
import { api, failWith, neverResolve } from '../../../.storybook/mocks/api'
import { sourcePriority, sources } from '../../../.storybook/mocks/settings/data'
import { ManageSourcesModal } from './ManageSourcesModal'

const meta = {
  component: ManageSourcesModal,
  tags: ['ai-generated'],
  parameters: { layout: 'fullscreen', msw: settingsMsw() },
  args: { opened: true, onClose: fn() },
} satisfies Meta<typeof ManageSourcesModal>

export default meta
type Story = StoryObj<typeof meta>

async function openDialog(canvasElement: HTMLElement) {
  const body = within(canvasElement.ownerDocument.body)
  const dialog = await body.findByRole('dialog')
  await waitFor(() => expect(dialog).toBeVisible())
  return within(dialog)
}

export const Catalogue: Story = {
  play: async ({ canvasElement }) => {
    const dialog = await openDialog(canvasElement)
    await expect(await dialog.findByRole('switch', { name: 'Enable MangaDex' })).toBeChecked()
    await expect(dialog.getByRole('switch', { name: 'Enable Weeb Central' })).not.toBeChecked()
    await expect(dialog.getByText('No changes yet')).toBeVisible()
    await expect(dialog.getByRole('button', { name: 'Save changes' })).toBeDisabled()
  },
}

export const SuwayomiProvider: Story = {
  args: { initialProvider: 'suwayomi' },
  play: async ({ canvasElement }) => {
    const dialog = await openDialog(canvasElement)
    await expect(await dialog.findByText('Multi Archive')).toBeVisible()
    await expect(dialog.queryByRole('switch', { name: 'Enable MangaDex' })).toBeNull()
    await expect(dialog.getByText('Several languages, pick one')).toBeVisible()
  },
}

export const SearchNarrowsTheList: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await openDialog(canvasElement)
    await dialog.findByRole('switch', { name: 'Enable MangaDex' })
    await userEvent.type(dialog.getByRole('searchbox', { name: 'Search sources' }), 'comick')
    await waitFor(() => expect(dialog.queryByRole('switch', { name: 'Enable MangaDex' })).toBeNull())
    await expect(dialog.getByRole('switch', { name: 'Enable Comick' })).toBeInTheDocument()
  },
}

export const SearchWithNoMatches: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await openDialog(canvasElement)
    await dialog.findByRole('switch', { name: 'Enable MangaDex' })
    await userEvent.type(dialog.getByRole('searchbox', { name: 'Search sources' }), 'zzzz')
    await expect(await dialog.findByText('No sources match. Clear a filter or try another name.')).toBeVisible()
  },
}

export const TurnASourceOn: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await openDialog(canvasElement)
    await userEvent.click(await dialog.findByRole('switch', { name: 'Enable Weeb Central' }))
    await expect(await dialog.findByText(/1 turned on/)).toBeVisible()
    await expect(dialog.getByRole('button', { name: 'Save changes' })).toBeEnabled()
  },
}

export const TurnASourceOff: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await openDialog(canvasElement)
    await userEvent.click(await dialog.findByRole('switch', { name: 'Enable Comick' }))
    await expect(await dialog.findByText(/1 turned off/)).toBeVisible()
  },
}

export const SaveChanges: Story = {
  play: async ({ args, canvasElement, userEvent }) => {
    const dialog = await openDialog(canvasElement)
    await userEvent.click(await dialog.findByRole('switch', { name: 'Enable Weeb Central' }))
    await userEvent.click(dialog.getByRole('button', { name: 'Save changes' }))
    await waitFor(() => expect(args.onClose).toHaveBeenCalled())
  },
}

export const DiscardPromptOnCancel: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await openDialog(canvasElement)
    await userEvent.click(await dialog.findByRole('switch', { name: 'Enable Weeb Central' }))
    await userEvent.click(dialog.getByRole('button', { name: 'Cancel' }))
    await expect(await within(canvasElement.ownerDocument.body).findByText('Discard changes?')).toBeVisible()
  },
}

export const CancelWithoutChangesClosesStraightAway: Story = {
  play: async ({ args, canvasElement, userEvent }) => {
    const dialog = await openDialog(canvasElement)
    await userEvent.click(await dialog.findByRole('button', { name: 'Cancel' }))
    await waitFor(() => expect(args.onClose).toHaveBeenCalled())
  },
}

export const ResetToDefaults: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await openDialog(canvasElement)
    await dialog.findByRole('switch', { name: 'Enable MangaDex' })
    await userEvent.click(dialog.getByRole('button', { name: 'Reset to defaults' }))
    await expect(await dialog.findByText(/turned (on|off)|Order changed/)).toBeVisible()
  },
}

export const FilterByState: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await openDialog(canvasElement)
    await dialog.findByRole('switch', { name: 'Enable MangaDex' })
    await userEvent.click(dialog.getByRole('radio', { name: 'Disabled' }))
    await waitFor(() => expect(dialog.queryByRole('switch', { name: 'Enable MangaDex' })).toBeNull())
    await expect(dialog.getByRole('switch', { name: 'Enable Weeb Central' })).toBeInTheDocument()
  },
}

export const PriorityRailMoveDown: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await openDialog(canvasElement)
    await userEvent.click(await dialog.findByRole('button', { name: 'Move MangaDex down' }))
    await expect(await dialog.findByText('Order changed')).toBeVisible()
  },
}

export const PhoneLayoutSwitchesPane: Story = {
  parameters: { viewport: { defaultViewport: 'mobile1' } },
  globals: { viewport: { value: 'mobile1', isRotated: false } },
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await openDialog(canvasElement)
    // The pane resets to the catalogue once the lists seed, so wait for them before switching.
    await dialog.findByRole('switch', { name: 'Enable MangaDex' })
    await userEvent.click(dialog.getByRole('tab', { name: 'Priority' }))
    await expect(await dialog.findByRole('button', { name: 'Move MangaDex down' })).toBeInTheDocument()
  },
}

export const NothingEnabled: Story = {
  parameters: {
    msw: settingsMsw(
      http.get(api('/settings/sources/priority'), () =>
        HttpResponse.json({ ...sourcePriority, disabled: sourcePriority.order }),
      ),
      http.get(api('/search/sources'), () => HttpResponse.json(sources.map((s) => ({ ...s, enabled: false })))),
    ),
  },
  play: async ({ canvasElement }) => {
    const dialog = await openDialog(canvasElement)
    await expect(await dialog.findByText('Nothing enabled. Turn a source on to rank it.')).toBeVisible()
  },
}

export const Loading: Story = {
  parameters: { msw: settingsMsw(http.get(api('/search/sources'), () => neverResolve())) },
  play: async ({ canvasElement }) => {
    const dialog = await openDialog(canvasElement)
    await expect(dialog.getByRole('button', { name: 'Save changes' })).toBeDisabled()
  },
}

export const SourcesFailedToLoad: Story = {
  parameters: { msw: settingsMsw(http.get(api('/search/sources'), () => failWith(500))) },
  play: async ({ canvasElement }) => {
    const dialog = await openDialog(canvasElement)
    await expect(dialog.getByRole('button', { name: 'Save changes' })).toBeDisabled()
  },
}

export const Closed: Story = {
  args: { opened: false },
}
