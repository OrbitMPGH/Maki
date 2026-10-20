import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith, neverResolve } from '../../.storybook/mocks/api'
import { discoverPage, resetTabState } from '../../.storybook/mocks/discover'
import CreatorPage from './CreatorPage'

const at = (url: string) => ({ path: '/creator/:name', initialEntries: [url] })

const meta = {
  component: CreatorPage,
  tags: ['ai-generated'],
  beforeEach: resetTabState,
  parameters: { layout: 'fullscreen', router: at('/creator/Aoi%20Mizuhara?role=author'), msw: discoverPage() },
} satisfies Meta<typeof CreatorPage>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { name: 'Aoi Mizuhara' })).toBeVisible()
    await expect(await canvas.findByText('12 titles in the catalogue')).toBeVisible()
  },
}

export const Following: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('button', { name: 'Following' })).toBeVisible()
  },
}

export const FollowThisCreator: Story = {
  parameters: { router: at('/creator/Haru%20Takeda?role=author') },
  play: async ({ canvas, userEvent }) => {
    const follow = await canvas.findByRole('button', { name: 'Follow' })
    await waitFor(() => expect(follow).toBeEnabled())
    await userEvent.click(follow)
    await expect(await canvas.findByRole('button', { name: 'Following' })).toBeVisible()
  },
}

export const Studio: Story = {
  parameters: { router: at('/creator/Studio%20Lark?role=studio') },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { name: 'Studio Lark' })).toBeVisible()
  },
}

export const FiltersOpen: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Filters' }))
    await expect(await canvas.findByText('Genres and tags')).toBeVisible()
  },
}

export const Loading: Story = {
  parameters: { msw: discoverPage(http.post(api('/recommendations/creator'), () => neverResolve())) },
}

export const NothingToShow: Story = {
  parameters: {
    msw: discoverPage(
      http.post(api('/recommendations/creator'), () =>
        HttpResponse.json({ name: 'Aoi Mizuhara', roles: ['author'], workCount: 4, items: [] }),
      ),
    ),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Nothing to show')).toBeVisible()
  },
}

export const UnknownCreator: Story = {
  parameters: { router: at('/creator/Nobody%20Atall') },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('No such creator')).toBeVisible()
  },
}

export const ServerError: Story = {
  parameters: {
    msw: discoverPage(http.post(api('/recommendations/creator'), () => failWith(500, 'The credit index is rebuilding'))),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Failed to load this creator')).toBeVisible()
    await expect(await canvas.findByRole('button', { name: 'Retry' })).toBeVisible()
  },
}

export const BlankName: Story = {
  parameters: { router: at('/creator/%20') },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Not a valid creator name')).toBeVisible()
  },
}

export const OpenedFromNotification: Story = {
  parameters: { router: at('/creator/Aoi%20Mizuhara?role=author&open=30000') },
  play: async ({ canvasElement }) => {
    const dialog = await within(canvasElement.ownerDocument.body).findByRole('dialog')
    await expect(await within(dialog).findByRole('heading', { name: 'The Salt Cartographer' })).toBeVisible()
  },
}

export const ReaderView: Story = {
  parameters: { auth: 'reader' },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { name: 'Aoi Mizuhara' })).toBeVisible()
  },
}
