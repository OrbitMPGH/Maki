import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith } from '../../../.storybook/mocks/api'
import { customRails, homePage } from '../../../.storybook/mocks/home'
import { metadataSettings } from '../../../.storybook/mocks/shared'
import { CustomRailEditor, CustomRailForm } from './CustomRailEditor'

const meta = {
  component: CustomRailEditor,
  tags: ['ai-generated'],
  args: {
    onClose: fn(),
    onSaved: fn(),
    draft: { placement: 'home', spec: { source: 'library' } },
  },
  parameters: { msw: homePage() },
} satisfies Meta<typeof CustomRailEditor>

export default meta
type Story = StoryObj<typeof meta>

const dialog = (canvasElement: HTMLElement, name: string) =>
  within(canvasElement.ownerDocument.body).findByRole('dialog', { name })

export const NewRail: Story = {
  play: async ({ canvasElement }) => {
    const modal = await dialog(canvasElement, 'New rail')
    await expect(await within(modal).findByRole('textbox', { name: 'Name' })).toBeVisible()
    await expect(await within(modal).findByText('128 titles match')).toBeVisible()
  },
}

export const EditLibraryRail: Story = {
  args: { rail: customRails[0], draft: undefined },
  play: async ({ canvasElement }) => {
    const modal = await dialog(canvasElement, 'Edit rail')
    await waitFor(() => expect(within(modal).getByRole('textbox', { name: 'Name' })).toHaveValue('Unread favourites'))
  },
}

export const EditRecommendationsRail: Story = {
  args: { rail: customRails[1], draft: undefined },
  play: async ({ canvasElement }) => {
    const modal = await dialog(canvasElement, 'Edit rail')
    await expect(await within(modal).findByText('Seeded from')).toBeVisible()
    await expect(await within(modal).findByText(/Picks for you/)).toBeVisible()
  },
}

export const EditCatalogueRail: Story = {
  args: { rail: customRails[2], draft: undefined },
  play: async ({ canvasElement }) => {
    const modal = await dialog(canvasElement, 'Edit rail')
    await waitFor(() => expect(within(modal).getByLabelText('Leave out series in your library')).toBeChecked())
  },
}

export const NewDiscoverRail: Story = {
  args: { draft: { placement: 'discover', spec: { source: 'recommendations' } } },
  play: async ({ canvasElement }) => {
    const modal = await dialog(canvasElement, 'New rail')
    // A library rail can't go on Discover, so that source is unavailable.
    await waitFor(() => expect(within(modal).getByRole('radio', { name: 'Your library' })).toBeDisabled())
  },
}

export const CopyOfExistingRail: Story = {
  args: { draft: { name: 'Cozy picks', placement: 'discover', spec: customRails[1].spec } },
  play: async ({ canvasElement }) => {
    const modal = await dialog(canvasElement, 'New rail')
    await waitFor(() => expect(within(modal).getByRole('textbox', { name: 'Name' })).toHaveValue('Cozy picks'))
  },
}

export const WithoutLocalDatabase: Story = {
  parameters: {
    msw: homePage(
      http.get(api('/settings/metadata'), () => HttpResponse.json({ ...metadataSettings, dumpPresent: false })),
    ),
  },
  play: async ({ canvasElement }) => {
    const modal = await dialog(canvasElement, 'New rail')
    await waitFor(() => expect(within(modal).getByRole('radio', { name: 'Catalogue' })).toBeDisabled())
    await waitFor(() => expect(within(modal).getByRole('radio', { name: 'Discover' })).toBeDisabled())
  },
}

export const NameRequired: Story = {
  play: async ({ canvasElement, userEvent, args }) => {
    const modal = await dialog(canvasElement, 'New rail')
    await userEvent.click(await within(modal).findByRole('button', { name: 'Add rail' }))
    await expect(await within(modal).findByText('Give it a name')).toBeVisible()
    await waitFor(() => expect(args.onSaved).not.toHaveBeenCalled())
  },
}

export const CreatesRail: Story = {
  play: async ({ canvasElement, userEvent, args }) => {
    const modal = await dialog(canvasElement, 'New rail')
    await userEvent.type(await within(modal).findByRole('textbox', { name: 'Name' }), 'Weekend reads')
    await userEvent.click(await within(modal).findByRole('button', { name: 'Add rail' }))
    await waitFor(() => expect(args.onSaved).toHaveBeenCalledOnce())
    await waitFor(() => expect(args.onClose).toHaveBeenCalled())
    await expect(await within(canvasElement.ownerDocument.body).findByText('Rail added')).toBeVisible()
  },
}

export const SaveFails: Story = {
  parameters: { msw: homePage(http.post(api('/rails'), () => failWith(500, 'Could not save the rail'))) },
  play: async ({ canvasElement, userEvent, args }) => {
    const modal = await dialog(canvasElement, 'New rail')
    await userEvent.type(await within(modal).findByRole('textbox', { name: 'Name' }), 'Weekend reads')
    await userEvent.click(await within(modal).findByRole('button', { name: 'Add rail' }))
    await expect(await within(modal).findByText('Could not save the rail')).toBeVisible()
    await waitFor(() => expect(args.onSaved).not.toHaveBeenCalled())
  },
}

export const SwitchSourceToCatalogue: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const modal = await dialog(canvasElement, 'New rail')
    const catalogue = await within(modal).findByRole('radio', { name: 'Catalogue' })
    await waitFor(() => expect(catalogue).toBeEnabled())
    await userEvent.click(catalogue)
    await expect(await within(modal).findByLabelText('Leave out series in your library')).toBeInTheDocument()
    await expect(await within(modal).findByText(/Anything in the MangaBaka catalogue/)).toBeVisible()
  },
}

export const FormInDrawer: Story = {
  render: (args) => (
    <div style={{ maxWidth: 640 }}>
      <CustomRailForm rail={customRails[2]} onSaved={args.onSaved} onCancel={args.onClose} placementLocked />
    </div>
  ),
  parameters: { layout: 'padded' },
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getByRole('textbox', { name: 'Name' })).toHaveValue('Top rated romance'))
    await expect(canvas.queryByText('Show on')).toBeNull()
    await expect(await canvas.findByRole('button', { name: 'Save' })).toBeVisible()
  },
}
