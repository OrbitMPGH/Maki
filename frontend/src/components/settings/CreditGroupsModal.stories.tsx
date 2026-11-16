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
    await expect(await dialog.findByText('Also read as PAPER CRNE, Paper Crone')).toBeVisible()
    const give = await dialog.findByRole('button', { name: 'Give to chapters' })
    await waitFor(() => expect(give).toBeDisabled())
    await expect(await dialog.findByText('Confirm the name before giving it to chapters.')).toBeVisible()
    await userEvent.click(await dialog.findByRole('button', { name: 'Confirm name' }))
    await waitFor(() => expect(onName).toHaveBeenCalledWith({ noticeIds: [4, 5], name: 'Paper Crane' }))
  },
}

const onJoinedConfirm = fn()

/** A notice with no name of its own whose text matches a group's is listed there, waiting for a confirm. */
export const JoinedByText: Story = {
  parameters: {
    msw: settingsMsw(
      http.put(api('/creditgroups/notices/:id'), async ({ params, request }) => {
        onJoinedConfirm(Number(params.id), await request.json())
        return HttpResponse.json({ named: 1 })
      }),
    ),
  },
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await dialogOf(canvasElement)
    await expect(await dialog.findByText('1 to confirm')).toBeVisible()
    await expect(await dialog.findByRole('button', { name: 'Confirm name' })).toBeVisible()
    const notice = within(await dialog.findByRole('article', { name: 'Notice 11' }))
    await expect(await notice.findByText('Reads like Moonlit Scans')).toBeVisible()
    await userEvent.click(await notice.findByRole('button', { name: 'Confirm' }))
    await waitFor(() => expect(onJoinedConfirm).toHaveBeenCalledWith(11, { groupName: 'Moonlit Scans' }))
  },
}

const onConfirmRest = fn()

/** A confirmed group with a joined notice still confirms: the name goes to every notice in it. */
export const ConfirmJoinedNotices: Story = {
  parameters: {
    msw: settingsMsw(
      http.put(api('/creditgroups/name'), async ({ request }) => {
        const body = (await request.json()) as { noticeIds: number[]; name: string }
        onConfirmRest(body)
        return HttpResponse.json({ named: body.noticeIds.length })
      }),
    ),
  },
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await dialogOf(canvasElement)
    await userEvent.click(await dialog.findByRole('button', { name: 'Confirm name' }))
    await waitFor(() => expect(onConfirmRest).toHaveBeenCalledWith({ noticeIds: [1, 2, 11], name: 'Moonlit Scans' }))
  },
}

const onAlikeName = fn()

/** Notices with no name that read alike are one group, so naming it once names them all. */
export const UnnamedGroupReadsAlike: Story = {
  parameters: {
    msw: settingsMsw(
      http.put(api('/creditgroups/name'), async ({ request }) => {
        const body = (await request.json()) as { noticeIds: number[]; name: string }
        onAlikeName(body)
        return HttpResponse.json({ named: body.noticeIds.length })
      }),
    ),
  },
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await dialogOf(canvasElement)
    await userEvent.click(await dialog.findByRole('button', { name: /^Unnamed group/ }))
    await userEvent.type(await dialog.findByRole('textbox', { name: 'Group name' }), 'Rook Scans')
    await userEvent.click(await dialog.findByRole('button', { name: 'Save name' }))
    await waitFor(() => expect(onAlikeName).toHaveBeenCalledWith({ noticeIds: [12, 13], name: 'Rook Scans' }))
  },
}

const onMerge = fn()

/** Two groups that are one: every notice of both takes the name of the group merged into. */
export const MergeGroups: Story = {
  parameters: {
    msw: settingsMsw(
      http.put(api('/creditgroups/name'), async ({ request }) => {
        const body = (await request.json()) as { noticeIds: number[]; name: string }
        onMerge(body)
        return HttpResponse.json({ named: body.noticeIds.length })
      }),
    ),
  },
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await dialogOf(canvasElement)
    await userEvent.click(await dialog.findByRole('button', { name: /^Paper Crane/ }))
    await userEvent.click(await dialog.findByRole('button', { name: 'Merge into another group' }))
    const body = within(canvasElement.ownerDocument.body)
    const targets = within(await body.findByRole('group', { name: 'Merge Paper Crane into' }))
    await expect(targets.queryByRole('button', { name: /^Paper Crane/ })).toBeNull()
    const merge = await body.findByRole('button', { name: 'Merge' })
    await waitFor(() => expect(merge).toBeDisabled())
    await userEvent.click(await targets.findByRole('button', { name: /^Moonlit Scans/ }))
    await userEvent.click(await body.findByRole('button', { name: 'Merge into Moonlit Scans' }))
    await waitFor(() => expect(onMerge).toHaveBeenCalledWith({ noticeIds: [4, 5, 1, 2, 11], name: 'Moonlit Scans' }))
    await expect(await body.findByText('Merged Paper Crane into Moonlit Scans')).toBeVisible()
  },
}

const manyGroups = {
  groups: [
    {
      key: 'group:qmega scans',
      name: 'QMEGA SCANS',
      nameSource: 'suggested',
      kind: 'unknown',
      noticeIds: [4, 5],
      notices: 2,
      series: 1,
      chapters: 40,
      chaptersWithoutGroup: 0,
      variants: [],
      joinedNoticeIds: [],
      unconfirmed: 0,
    },
    ...Array.from({ length: 28 }, (_, i) => ({
      key: `group:team ${i + 1}`,
      name: `Team ${i + 1}`,
      nameSource: 'confirmed',
      kind: 'group',
      noticeIds: [3],
      notices: 1,
      series: 1,
      chapters: 30 - i,
      chaptersWithoutGroup: 0,
      variants: [],
      joinedNoticeIds: [],
      unconfirmed: 0,
    })),
    {
      key: 'group:omega scans',
      name: 'Omega Scans',
      nameSource: 'confirmed',
      kind: 'group',
      noticeIds: [1, 2],
      notices: 2,
      series: 2,
      chapters: 2,
      chaptersWithoutGroup: 0,
      variants: [],
      joinedNoticeIds: [],
      unconfirmed: 0,
    },
  ],
}

/** With dozens of groups the list scrolls at full row height, leads with the closest name and can be filtered. */
export const MergeListWithManyGroups: Story = {
  parameters: {
    msw: settingsMsw(http.get(api('/creditgroups'), () => HttpResponse.json(manyGroups))),
  },
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await dialogOf(canvasElement)
    await userEvent.click(await dialog.findByRole('button', { name: 'Merge into another group' }))
    const body = within(canvasElement.ownerDocument.body)
    const list = await body.findByRole('group', { name: 'Merge QMEGA SCANS into' })
    const rows = await within(list).findAllByRole('button')
    await waitFor(() => expect(rows).toHaveLength(29))
    await expect(rows[0]).toHaveTextContent('Omega Scans')
    await waitFor(() => expect(rows[0].getBoundingClientRect().height).toBeGreaterThanOrEqual(24))
    await waitFor(() => expect(list.scrollHeight).toBeGreaterThan(list.clientHeight))
    await userEvent.type(await body.findByRole('textbox', { name: 'Find a group' }), 'team 2')
    await waitFor(() => expect(within(list).getAllByRole('button')).toHaveLength(10))
  },
}

/** A notice's image opens larger in place, from the wider rendition. */
export const EnlargedImage: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await dialogOf(canvasElement)
    await userEvent.click((await dialog.findAllByRole('button', { name: 'Enlarge the image' }))[0])
    const large = within(await within(canvasElement.ownerDocument.body).findByRole('dialog', { name: 'Notice' }))
    const image = await large.findByRole('img')
    await waitFor(() =>
      expect(image).toHaveAttribute('src', expect.stringContaining('/creditgroups/notices/1/image?large=true')),
    )
    await userEvent.keyboard('{Escape}')
    await waitFor(() =>
      expect(within(canvasElement.ownerDocument.body).queryByRole('dialog', { name: 'Notice' })).toBeNull(),
    )
    await expect(await dialog.findByRole('region', { name: 'Moonlit Scans' })).toBeVisible()
  },
}

/** Escape closes a popover on top of the modal, not the modal under it. */
export const EscapeClosesOnlyThePopover: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await dialogOf(canvasElement)
    await userEvent.click(await dialog.findByRole('button', { name: 'Merge into another group' }))
    const body = within(canvasElement.ownerDocument.body)
    await expect(await body.findByRole('group', { name: 'Merge Moonlit Scans into' })).toBeVisible()
    await userEvent.keyboard('{Escape}')
    await waitFor(() => expect(body.queryByRole('group', { name: 'Merge Moonlit Scans into' })).toBeNull())
    await expect(await dialog.findByRole('region', { name: 'Moonlit Scans' })).toBeVisible()
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
