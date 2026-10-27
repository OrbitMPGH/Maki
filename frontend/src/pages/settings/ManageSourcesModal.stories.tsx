import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { settingsMsw } from '../../../.storybook/mocks/settings'
import { api, failWith, neverResolve } from '../../../.storybook/mocks/api'
import {
  customSourceInfos, customSourcesList, sourcePriority, sources,
} from '../../../.storybook/mocks/settings/data'
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
    await waitFor(() => expect(dialog.getByRole('switch', { name: 'Enable MangaDex' })).toBeChecked())
    await waitFor(() => expect(dialog.getByRole('switch', { name: 'Enable Weeb Central' })).not.toBeChecked())
    await expect(await dialog.findByText('No changes yet')).toBeVisible()
    await waitFor(() => expect(dialog.getByRole('button', { name: 'Save changes' })).toBeDisabled())
  },
}

export const SuwayomiProvider: Story = {
  args: { initialProvider: 'suwayomi' },
  play: async ({ canvasElement }) => {
    const dialog = await openDialog(canvasElement)
    await expect(await dialog.findByText('Multi Archive')).toBeVisible()
    await expect(dialog.queryByRole('switch', { name: 'Enable MangaDex' })).toBeNull()
    await expect(await dialog.findByText('Several languages, pick one')).toBeVisible()
  },
}

export const SearchNarrowsTheList: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await openDialog(canvasElement)
    await dialog.findByRole('switch', { name: 'Enable MangaDex' })
    await userEvent.type(await dialog.findByRole('searchbox', { name: 'Search sources' }), 'comick')
    await waitFor(() => expect(dialog.queryByRole('switch', { name: 'Enable MangaDex' })).toBeNull())
    await expect(await dialog.findByRole('switch', { name: 'Enable Comick' })).toBeInTheDocument()
  },
}

export const SearchWithNoMatches: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await openDialog(canvasElement)
    await dialog.findByRole('switch', { name: 'Enable MangaDex' })
    await userEvent.type(await dialog.findByRole('searchbox', { name: 'Search sources' }), 'zzzz')
    await expect(await dialog.findByText('No sources match. Clear a filter or try another name.')).toBeVisible()
  },
}

export const TurnASourceOn: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await openDialog(canvasElement)
    await userEvent.click(await dialog.findByRole('switch', { name: 'Enable Weeb Central' }))
    await expect(await dialog.findByText(/1 turned on/)).toBeVisible()
    await waitFor(() => expect(dialog.getByRole('button', { name: 'Save changes' })).toBeEnabled())
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
    await userEvent.click(await dialog.findByRole('button', { name: 'Save changes' }))
    await waitFor(() => expect(args.onClose).toHaveBeenCalled())
  },
}

export const DiscardPromptOnCancel: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await openDialog(canvasElement)
    await userEvent.click(await dialog.findByRole('switch', { name: 'Enable Weeb Central' }))
    await userEvent.click(await dialog.findByRole('button', { name: 'Cancel' }))
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
    await userEvent.click(await dialog.findByRole('button', { name: 'Reset to defaults' }))
    await expect(await dialog.findByText(/turned (on|off)|Order changed/)).toBeVisible()
  },
}

export const FilterByState: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await openDialog(canvasElement)
    await dialog.findByRole('switch', { name: 'Enable MangaDex' })
    await userEvent.click(await dialog.findByRole('radio', { name: 'Disabled' }))
    await waitFor(() => expect(dialog.queryByRole('switch', { name: 'Enable MangaDex' })).toBeNull())
    await expect(await dialog.findByRole('switch', { name: 'Enable Weeb Central' })).toBeInTheDocument()
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
    await userEvent.click(await dialog.findByRole('tab', { name: 'Priority' }))
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
    await waitFor(() => expect(dialog.getByRole('button', { name: 'Save changes' })).toBeDisabled())
  },
}

export const SourcesFailedToLoad: Story = {
  parameters: { msw: settingsMsw(http.get(api('/search/sources'), () => failWith(500))) },
  play: async ({ canvasElement }) => {
    const dialog = await openDialog(canvasElement)
    await waitFor(() => expect(dialog.getByRole('button', { name: 'Save changes' })).toBeDisabled())
  },
}

const withCustomSources = () => settingsMsw(
  http.get(api('/search/sources'), () => HttpResponse.json([...sources, ...customSourceInfos])),
  http.get(api('/settings/sources/priority'), () =>
    HttpResponse.json({ ...sourcePriority, order: [...sourcePriority.order, 'custom-quietpen', 'custom-later'] }),
  ),
  http.get(api('/custom-sources'), () => HttpResponse.json(customSourcesList)),
)

export const CustomSources: Story = {
  parameters: { msw: withCustomSources() },
  play: async ({ canvasElement }) => {
    const dialog = await openDialog(canvasElement)
    await expect(await dialog.findByRole('switch', { name: 'Enable Quiet Pen' })).toBeInTheDocument()
    await expect(await dialog.findByText('Link by URL')).toBeVisible()
    await expect(await dialog.findByText('Not working')).toBeVisible()
    await expect(await dialog.findByRole('button', { name: 'More for Quiet Pen' })).toBeVisible()
  },
}

export const CustomSourceProviderFilter: Story = {
  args: { initialProvider: 'custom' },
  parameters: { msw: withCustomSources() },
  play: async ({ canvasElement }) => {
    const dialog = await openDialog(canvasElement)
    await expect(await dialog.findByRole('switch', { name: 'Enable Quiet Pen' })).toBeInTheDocument()
    await expect(dialog.queryByRole('switch', { name: 'Enable MangaDex' })).toBeNull()
  },
}

export const ImportACustomSource: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await openDialog(canvasElement)
    await userEvent.click(await dialog.findByRole('button', { name: 'Add source' }))
    await userEvent.click(await body.findByRole('menuitem', { name: 'Import definition' }))
    const editor = await body.findByRole('textbox', { name: 'Definition' })
    await userEvent.click(editor)
    await userEvent.paste(JSON.stringify(customSourcesList[0].definition))
    await waitFor(() => expect(body.getByRole('button', { name: 'Save' })).toBeDisabled())
    await userEvent.click(await body.findByRole('button', { name: 'Test' }))
    await expect(await body.findByText('Every check passed')).toBeVisible()
    await waitFor(() => expect(body.getByRole('button', { name: 'Save' })).toBeEnabled())
  },
}

export const ReaderSeesNoAddButton: Story = {
  parameters: { auth: 'reader' },
  play: async ({ canvasElement }) => {
    const dialog = await openDialog(canvasElement)
    await dialog.findByRole('switch', { name: 'Enable MangaDex' })
    await expect(dialog.queryByRole('button', { name: 'Add source' })).toBeNull()
  },
}

export const Closed: Story = {
  args: { opened: false },
}
