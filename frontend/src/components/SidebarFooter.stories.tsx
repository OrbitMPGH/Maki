import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api } from '../../.storybook/mocks/api'
import { updateAvailable } from '../../.storybook/mocks/shell'
import SidebarFooter from './layout/SidebarFooter'

const STAR_KEY = 'star-nudge-dismissed'
const SKIP_KEY = 'update-skipped-version'

// Both nudges remember a dismissal in localStorage, which outlives a story.
function resetStorage() {
  try {
    localStorage.removeItem(STAR_KEY)
    localStorage.removeItem(SKIP_KEY)
    localStorage.removeItem('update-banner-dismissed')
  } catch {
    // Storage blocked: the footer falls back to in-memory state.
  }
}

const meta = {
  component: SidebarFooter,
  tags: ['ai-generated'],
  parameters: { layout: 'centered' },
  decorators: [
    (Story) => (
      <div style={{ width: 260, paddingTop: 280 }}>
        <Story />
      </div>
    ),
  ],
  beforeEach: () => {
    resetStorage()
  },
} satisfies Meta<typeof SidebarFooter>

export default meta
type Story = StoryObj<typeof meta>

const withUpdate = http.get(api('/system/update'), () => HttpResponse.json(updateAvailable))

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Star on GitHub')).toBeVisible()
    await expect(await canvas.findByText('v0.32.0')).toBeVisible()
  },
}

export const StarDismissed: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Dismiss' }))
    await expect(canvas.queryByText('Star on GitHub')).toBeNull()
    await expect(canvas.getByText('Help & feedback')).toBeVisible()
  },
}

export const UpdateAvailable: Story = {
  parameters: { msw: [withUpdate] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Update 0.33.0 available')).toBeVisible()
    await expect(canvas.queryByText('Star on GitHub')).toBeNull()
  },
}

export const UpdateSkipped: Story = {
  parameters: { msw: [withUpdate] },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Skip 0.33.0' }))
    // Skipping puts the star nudge back and leaves a dot on the version.
    await expect(await canvas.findByText('Star on GitHub')).toBeVisible()
    await expect(canvas.queryByText('Update 0.33.0 available')).toBeNull()
  },
}

export const UpdateHiddenFromReaders: Story = {
  parameters: { auth: 'reader', msw: [withUpdate] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Star on GitHub')).toBeVisible()
    await expect(canvas.queryByText('Update 0.33.0 available')).toBeNull()
  },
}

export const DevBuild: Story = {
  parameters: {
    msw: [http.get(api('/system/status'), () => HttpResponse.json({ version: '0.33.0-dev.14' }))],
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('v0.33.0-dev.14')).toBeVisible()
  },
}

export const HelpMenuOpen: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: /Help & feedback/ }))
    const body = within(canvasElement.ownerDocument.body)
    await waitFor(async () => expect(await body.findByRole('menuitem', { name: /Report a bug/ })).toBeVisible())
    await expect(body.getByRole('menuitem', { name: /Documentation/ })).toBeVisible()
    await expect(body.getByRole('menuitem', { name: /What's new in v0.32.0/ })).toBeVisible()
  },
}
