import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import '@mantine/charts/styles.css'
import { api, failWith, neverResolve } from '../../../../.storybook/mocks/api'
import { libraryFor, libraryGrowthHandler, libraryHandler, sectionProps } from '../../../../.storybook/mocks/insights'
import LibrarySection from './LibrarySection'

const meta = {
  component: LibrarySection,
  tags: ['ai-generated'],
  parameters: { layout: 'padded' },
  decorators: [
    (Story) => (
      <div className="stats-layout">
        <Story />
      </div>
    ),
  ],
  args: sectionProps,
} satisfies Meta<typeof LibrarySection>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Growth')).toBeVisible()
    await expect(await canvas.findByText('Composition')).toBeVisible()
    await expect(await canvas.findByText('Biggest on disk')).toBeVisible()
    await expect(await canvas.findByText('Genres in the library')).toBeVisible()
    await expect(await canvas.findByText(/delivered/)).toBeVisible()
  },
}

/** A reader sees only their own requests rather than everybody's. */
export const ReaderRequests: Story = {
  parameters: {
    msw: [
      http.get(api('/stats/library'), () =>
        HttpResponse.json({ ...libraryFor('rich'), requests: { allUsers: false, open: 1, resolved90d: 2, medianResolveHours: null } }),
      ),
    ],
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Your requests')).toBeVisible()
  },
}

export const EmptyLibrary: Story = {
  parameters: { msw: [libraryHandler('empty'), libraryGrowthHandler('empty')] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Nothing to show yet.')).toBeVisible()
    await waitFor(() => expect(canvas.getAllByText('Nothing downloaded yet.').length).toBeGreaterThan(0))
  },
}

/** Twelve months goes by month; the default 30 day window goes by day. */
export const TwelveMonths: Story = {
  args: {
    range: { from: '2025-10-20', to: '2026-10-19' },
    previous: { from: '2024-10-20', to: '2025-10-19' },
    preset: '12m',
    windowLabel: 'the last 12 months',
  },
}

export const Loading: Story = {
  parameters: { msw: [http.get(api('/stats/library'), () => neverResolve())] },
}

export const LoadFailed: Story = {
  parameters: { msw: [http.get(api('/stats/library'), () => failWith(500))] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Could not load library stats')).toBeVisible()
    await expect(await canvas.findByRole('button', { name: 'Try again' })).toBeVisible()
  },
}
