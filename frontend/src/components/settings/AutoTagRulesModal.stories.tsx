import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api } from '../../../.storybook/mocks/api'
import { settingsMsw } from '../../../.storybook/mocks/settings'
import { AutoTagRulesModal } from './AutoTagRulesModal'

const meta = {
  component: AutoTagRulesModal,
  tags: ['ai-generated'],
  args: { opened: true, onClose: fn() },
  parameters: { layout: 'fullscreen', msw: settingsMsw() },
} satisfies Meta<typeof AutoTagRulesModal>

export default meta
type Story = StoryObj<typeof meta>

const dialogIn = (canvasElement: HTMLElement) =>
  within(canvasElement.ownerDocument.body).findByRole('dialog', { name: 'Auto-tag rules' })

export const Default: Story = {
  play: async ({ canvasElement }) => {
    const dialog = await dialogIn(canvasElement)
    await expect(await within(dialog).findByText('Weekend read')).toBeVisible()
    await expect(await within(dialog).findByText('Type: Manhwa · How it was added: Import list')).toBeVisible()
  },
}

/** A rule with alternatives reads as either one. */
export const RuleWithAlternatives: Story = {
  play: async ({ canvasElement }) => {
    const dialog = await dialogIn(canvasElement)
    await expect(await within(dialog).findByText('Content rating: Suggestive or Genre: Romance')).toBeVisible()
  },
}

export const Empty: Story = {
  parameters: { msw: settingsMsw(http.get(api('/tags/rules'), () => HttpResponse.json([]))) },
  play: async ({ canvasElement }) => {
    const dialog = await dialogIn(canvasElement)
    await expect(await within(dialog).findByText('No rules yet')).toBeVisible()
  },
}

const onSave = fn()

export const AddRule: Story = {
  parameters: {
    msw: settingsMsw(
      http.post(api('/tags/rules'), async ({ request }) => {
        const body = (await request.json()) as object
        onSave(body)
        return HttpResponse.json({ id: 9, ...body })
      }),
    ),
  },
  play: async ({ canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await dialogIn(canvasElement)
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Add rule' }))

    const editor = await body.findByRole('dialog', { name: 'New rule' })
    const save = await within(editor).findByRole('button', { name: 'Save rule' })
    await waitFor(() => expect(save).toBeDisabled())

    await userEvent.click(await within(editor).findByRole('button', { name: /Tag to add/ }))
    await userEvent.click(await within(editor).findByRole('radio', { name: 'Favourites' }))
    await userEvent.click(await within(editor).findByRole('button', { name: /^Type/ }))
    await userEvent.click(await within(editor).findByRole('radio', { name: 'Manhua' }))

    await waitFor(() => expect(save).toBeEnabled())
    await userEvent.click(save)
    await waitFor(() =>
      expect(onSave).toHaveBeenCalledWith({
        tagId: 1,
        enabled: true,
        groups: [[{ field: 'type', value: 'manhua' }]],
      }),
    )
  },
}
