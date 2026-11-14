import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api } from '../../../.storybook/mocks/api'
import { settingsMsw } from '../../../.storybook/mocks/settings'
import { CreditGroupsModal } from './CreditGroupsModal'

const meta = {
  component: CreditGroupsModal,
  tags: ['ai-generated'],
  args: { opened: true, onClose: fn() },
  parameters: { layout: 'fullscreen', msw: settingsMsw() },
} satisfies Meta<typeof CreditGroupsModal>

export default meta
type Story = StoryObj<typeof meta>

const dialogOf = async (canvasElement: HTMLElement) =>
  within(await within(canvasElement.ownerDocument.body).findByRole('dialog', { name: 'Release groups' }))

/** The first group opens beside the list: its notices, their controls and the chapters carrying them. */
export const Default: Story = {
  play: async ({ canvasElement }) => {
    const dialog = await dialogOf(canvasElement)
    await expect(await dialog.findByRole('region', { name: 'Moonlit Scans' })).toBeVisible()
    await expect(await dialog.findByText(/We are recruiting typesetters/)).toBeVisible()
    await expect(await dialog.findByText('Bottom 28% of a page')).toBeVisible()
    await expect((await dialog.findAllByText('Lanterns Over Kasumi'))[0]).toBeVisible()
    await expect(await dialog.findByRole('button', { name: 'Give to 12 chapters' })).toBeEnabled()
    await expect(await dialog.findByRole('button', { name: /^Site ads/ })).toBeVisible()
  },
}

const onName = fn()

/** A name read off the image is a suggestion: giving it to chapters waits until it is confirmed. */
export const SuggestedName: Story = {
  parameters: {
    msw: settingsMsw(
      http.put(api('/creditgroups/name'), async ({ request }) => {
        const body = (await request.json()) as { noticeIds: number[]; name: string }
        onName(body)
        return HttpResponse.json({ named: body.noticeIds.length })
      }),
    ),
  },
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await dialogOf(canvasElement)
    await userEvent.click(await dialog.findByRole('button', { name: /^Paper Crane/ }))
    await expect(await dialog.findByText(/Read off the image text/)).toBeVisible()
    const give = await dialog.findByRole('button', { name: 'Give to chapters' })
    await waitFor(() => expect(give).toBeDisabled())
    await expect(await dialog.findByText('Confirm the name before giving it to chapters.')).toBeVisible()
    await userEvent.click(await dialog.findByRole('button', { name: 'Confirm name' }))
    await waitFor(() => expect(onName).toHaveBeenCalledWith({ noticeIds: [4, 5], name: 'Paper Crane' }))
  },
}

const onUpdate = fn()

/** Hide, keep or leave it to Maki, per notice. */
export const OverrideNotice: Story = {
  parameters: {
    msw: settingsMsw(
      http.put(api('/creditgroups/notices/:id'), async ({ params, request }) => {
        onUpdate(Number(params.id), await request.json())
        return HttpResponse.json({
          id: Number(params.id),
          hidden: false,
          accepted: true,
          override: 'keep',
          kind: 'group',
          groupName: 'Moonlit Scans',
          groupSource: 'confirmed',
          suggestedName: 'Moonlit Scans',
          invite: 'moonlit',
          excerpt: 'MOONLIT SCANS | Translator: Hana | Cleaner: Ivo | Join us on Discord | Read it first on our site',
          whole: true,
          share: 1,
          tail: false,
          chapters: 26,
          series: 2,
        })
      }),
    ),
  },
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await dialogOf(canvasElement)
    const notice = within(await dialog.findByRole('article', { name: 'Notice 1' }))
    await userEvent.click(await notice.findByText('Kept'))
    await waitFor(() => expect(onUpdate).toHaveBeenCalledWith(1, { override: 'Keep' }))
  },
}

export const SiteAds: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await dialogOf(canvasElement)
    await userEvent.click(await dialog.findByRole('button', { name: /^Site ads/ }))
    await expect(await dialog.findByText(/Ads an aggregator site pastes into chapters/)).toBeVisible()
    await expect(await dialog.findByText('Site ads never give a group to chapters.')).toBeVisible()
  },
}

/** A notice no readable chapter carries any more shows a placeholder instead of a broken image. */
export const Unnamed: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await dialogOf(canvasElement)
    await userEvent.click(await dialog.findByRole('button', { name: /^No group name/ }))
    await expect(await dialog.findByText(/could not read a group name off these/)).toBeVisible()
    await expect(await dialog.findByText('No chapter that carries it can be read right now.')).toBeVisible()
  },
}

/** Applying renames files, so it asks first. */
export const ApplyConfirmation: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await dialogOf(canvasElement)
    await userEvent.click(await dialog.findByRole('button', { name: 'Give to 12 chapters' }))
    const body = within(canvasElement.ownerDocument.body)
    const confirm = within(await body.findByRole('dialog', { name: 'Give Moonlit Scans to chapters' }))
    await expect(await confirm.findByText(/Maki renames these files on disk/)).toBeVisible()
  },
}

export const ApplyResult: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await dialogOf(canvasElement)
    await userEvent.click(await dialog.findByRole('button', { name: 'Give to 12 chapters' }))
    const body = within(canvasElement.ownerDocument.body)
    const confirm = within(await body.findByRole('dialog', { name: 'Give Moonlit Scans to chapters' }))
    await userEvent.click(await confirm.findByRole('button', { name: 'Give to 12 chapters' }))
    const result = within(await body.findByRole('dialog', { name: 'Given to chapters' }))
    await expect(await result.findByText('Files renamed')).toBeVisible()
    await expect(await result.findByText(/name different groups/)).toBeVisible()
    await expect(await result.findByText(/open in the reader/)).toBeVisible()
  },
}

export const EveryNotice: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await dialogOf(canvasElement)
    await userEvent.click(await dialog.findByText('Every notice'))
    await expect(await dialog.findByRole('button', { name: /^Night Owl/ })).toBeVisible()
  },
}

export const Empty: Story = {
  parameters: { msw: settingsMsw(http.get(api('/creditgroups'), () => HttpResponse.json({ groups: [] }))) },
  play: async ({ canvasElement }) => {
    const dialog = await dialogOf(canvasElement)
    await expect(await dialog.findByText('No hidden notices yet')).toBeVisible()
  },
}

export const LoadFailed: Story = {
  parameters: {
    msw: settingsMsw(
      http.get(api('/creditgroups'), () => HttpResponse.json({ code: 'error.generic', error: 'Boom' }, { status: 500 })),
    ),
  },
  play: async ({ canvasElement }) => {
    const dialog = await dialogOf(canvasElement)
    await expect(await dialog.findByText("Couldn't load the release groups")).toBeVisible()
  },
}

/** On a phone the list and a group are drill-in pages. */
export const Phone: Story = {
  parameters: { viewport: { defaultViewport: 'mobile1' } },
  globals: { viewport: { value: 'mobile1', isRotated: false } },
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await dialogOf(canvasElement)
    await userEvent.click(await dialog.findByRole('button', { name: /^Kitsune Translations/ }))
    await expect(await dialog.findByText(/Taken from chapters that already carry this group/)).toBeVisible()
  },
}
