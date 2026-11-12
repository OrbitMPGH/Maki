import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api } from '../../../.storybook/mocks/api'
import { settingsMsw } from '../../../.storybook/mocks/settings'
import { RootFolderRulesModal } from './RootFolderRulesModal'

const meta = {
  component: RootFolderRulesModal,
  tags: ['ai-generated'],
  args: { opened: true, onClose: fn() },
  parameters: { layout: 'fullscreen', msw: settingsMsw() },
} satisfies Meta<typeof RootFolderRulesModal>

export default meta
type Story = StoryObj<typeof meta>

const dialogIn = (canvasElement: HTMLElement, name = 'Root folder rules') =>
  within(canvasElement.ownerDocument.body).findByRole('dialog', { name })

export const Default: Story = {
  play: async ({ canvasElement }) => {
    const dialog = await dialogIn(canvasElement)
    await expect(await within(dialog).findByText('/library/webtoons')).toBeVisible()
    await expect(await within(dialog).findByText('Type: Manhwa or Type: Manhua')).toBeVisible()
    await waitFor(() => expect(within(dialog).getAllByRole('button', { name: 'Move up' })[0]).toBeDisabled())
  },
}

export const Empty: Story = {
  parameters: { msw: settingsMsw(http.get(api('/rootfolder/rules'), () => HttpResponse.json([]))) },
  play: async ({ canvasElement }) => {
    const dialog = await dialogIn(canvasElement)
    await expect(await within(dialog).findByText('No rules yet')).toBeVisible()
  },
}

const onSave = fn()

/** "If it's a manhwa or a manhua, put it in this folder": two alternatives of one condition each. */
export const AddRuleWithAlternatives: Story = {
  parameters: {
    msw: settingsMsw(
      http.post(api('/rootfolder/rules'), async ({ request }) => {
        const body = (await request.json()) as object
        onSave(body)
        return HttpResponse.json({ id: 9, position: 2, ...body })
      }),
    ),
  },
  play: async ({ canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await dialogIn(canvasElement)
    const add = await within(dialog).findByRole('button', { name: 'Add rule' })
    await waitFor(() => expect(add).toBeEnabled())
    await userEvent.click(add)

    const editor = await body.findByRole('dialog', { name: 'New rule' })
    const save = await within(editor).findByRole('button', { name: 'Save rule' })
    await waitFor(() => expect(save).toBeDisabled())

    await userEvent.click(await within(editor).findByRole('button', { name: /^Put it in/ }))
    await userEvent.click(await within(editor).findByRole('radio', { name: '/library/webtoons' }))
    await userEvent.click(await within(editor).findByRole('button', { name: /^Type/ }))
    await userEvent.click(await within(editor).findByRole('radio', { name: 'Manhwa' }))
    await userEvent.click(await within(editor).findByRole('button', { name: 'Or match other conditions' }))
    await waitFor(() => expect(within(editor).getAllByRole('button', { name: /^Type/ })).toHaveLength(2))
    await userEvent.click(within(editor).getAllByRole('button', { name: /^Type/ })[1])
    await userEvent.click(await within(editor).findByRole('radio', { name: 'Manhua' }))

    await waitFor(() => expect(save).toBeEnabled())
    await userEvent.click(save)
    await waitFor(() =>
      expect(onSave).toHaveBeenCalledWith({
        rootFolderId: 2,
        enabled: true,
        groups: [[{ field: 'type', value: 'manhwa' }], [{ field: 'type', value: 'manhua' }]],
      }),
    )
  },
}

export const CheckASeries: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await dialogIn(canvasElement)
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Check a series' }))

    const check = await dialogIn(canvasElement, 'Check a series')
    const series = await within(check).findAllByRole('radio')
    await userEvent.click(series[0])
    await expect(await within(check).findByText(/would put/)).toBeVisible()
  },
}
