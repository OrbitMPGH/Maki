import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api } from '../../.storybook/mocks/api'
import { askSoFarSync, resetSoFarSyncAsk } from '../lib/soFarSyncAsk'
import { SoFarSyncAskDialog } from './SoFarSyncAskDialog'

let answer: { services: string[]; enabled: boolean; seriesId?: number } | null = null

const meta = {
  component: SoFarSyncAskDialog,
  tags: ['ai-generated'],
  parameters: {
    msw: [
      http.put(api('/scrobble/sofar'), async ({ request }) => {
        answer = (await request.json()) as typeof answer
        return HttpResponse.json(answer)
      }),
    ],
  },
  beforeEach: () => {
    answer = null
    askSoFarSync({
      trackers: [
        { service: 'anilist', label: 'AniList' },
        { service: 'mal', label: 'MyAnimeList' },
      ],
      seriesId: 3,
    })
    return resetSoFarSyncAsk
  },
} satisfies Meta<typeof SoFarSyncAskDialog>

export default meta
type Story = StoryObj<typeof meta>

export const SendThem: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await within(canvasElement.ownerDocument.body).findByRole('dialog')
    await expect(within(dialog).getByText('Send "so far" scores to AniList and MyAnimeList too?')).toBeVisible()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Send them' }))
    await waitFor(() => expect(answer).toEqual({ services: ['anilist', 'mal'], enabled: true, seriesId: 3 }))
  },
}

export const KeepThemInMaki: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await within(canvasElement.ownerDocument.body).findByRole('dialog')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Keep them in Maki' }))
    await waitFor(() => expect(answer?.enabled).toBe(false))
  },
}
