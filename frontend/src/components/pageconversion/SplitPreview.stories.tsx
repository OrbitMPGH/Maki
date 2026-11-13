import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api } from '../../../.storybook/mocks/api'
import { settingsMsw } from '../../../.storybook/mocks/settings'
import { SplitPreview } from './SplitPreview'

const meta = {
  component: SplitPreview,
  tags: ['ai-generated'],
  parameters: { msw: settingsMsw() },
  args: { height: 5000 },
} satisfies Meta<typeof SplitPreview>

export default meta
type Story = StoryObj<typeof meta>

export const CutsInTheGutters: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Tower of the Long Night')).toBeVisible()
    await expect(canvas.getByText(/Ch\.41, page 4, 15,?200 px tall/)).toBeVisible()
    await expect(await canvas.findByText('Becomes 4 pages. Each dashed line is a cut.')).toBeVisible()
    await waitFor(() => expect(canvas.getAllByTestId('split-cut')).toHaveLength(3))
    await expect(await canvas.findByRole('img', { name: 'Tallest page' })).toBeInTheDocument()
  },
}

export const OneCutAtTheMaximum: Story = {
  args: { height: 13000 },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Becomes 2 pages. Each dashed line is a cut.')).toBeVisible()
  },
}

export const NothingToPreview: Story = {
  parameters: { msw: settingsMsw(http.get(api('/pageconversion/split'), () => new HttpResponse(null, { status: 404 }))) },
  play: async ({ canvas }) => {
    await expect(
      await canvas.findByText('No page to preview yet. The preview uses the tallest page in your library.'),
    ).toBeVisible()
  },
}
