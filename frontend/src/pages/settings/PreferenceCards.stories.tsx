import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { settingsMsw } from '../../../.storybook/mocks/settings'
import { api, failWith } from '../../../.storybook/mocks/api'
import { uiSettings } from '../../../.storybook/mocks/shared'
import {
  AppearanceSection,
  DiscoverSection,
  HomeSectionsSection,
  LanguageSection,
  SeriesPageSection,
  TitleLanguageSection,
} from './PreferenceCards'

// One file for the group: the meta's component is the first card and the other stories render their
// own through `render`.
const meta = {
  component: DiscoverSection,
  tags: ['ai-generated'],
  parameters: { layout: 'padded', msw: settingsMsw() },
  decorators: [
    (Story) => (
      <div style={{ maxWidth: 820 }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof DiscoverSection>

export default meta
type Story = StoryObj<typeof meta>

export const ContentRating: Story = {
  play: async ({ canvas, userEvent }) => {
    const suggestive = await canvas.findByRole('button', { name: /^Suggestive/ })
    await waitFor(() => expect(suggestive).toHaveAttribute('aria-pressed', 'true'))
    await userEvent.click(await canvas.findByRole('button', { name: /^Erotica/ }))
  },
}

export const ContentRatingLoadFailed: Story = {
  parameters: { msw: settingsMsw(http.get(api('/settings/discover'), () => failWith(500))) },
  play: async ({ canvas }) => {
    // Falls back to the server default while the setting cannot be read.
    await waitFor(() => expect(canvas.getByRole('button', { name: /^Erotica/ })).toHaveAttribute('aria-pressed', 'true'))
  },
}

export const Language: Story = {
  render: () => <LanguageSection />,
  play: async ({ canvas }) => {
    await expect(await canvas.findByLabelText('Interface language')).toBeInTheDocument()
    await expect(await canvas.findByText(/Showing /)).toBeVisible()
  },
}

export const TitleLanguage: Story = {
  render: () => <TitleLanguageSection />,
  parameters: {
    msw: settingsMsw(http.get(api('/settings/ui'), () => HttpResponse.json({ ...uiSettings, titleLanguage: 'ja,en' }))),
  },
  play: async ({ canvas }) => {
    const select = await canvas.findByLabelText('Title language', { selector: 'input' })
    await waitFor(() => expect(select).toHaveValue('Japanese'))
  },
}

export const SeriesPage: Story = {
  render: () => <SeriesPageSection />,
  parameters: {
    msw: settingsMsw(
      http.get(api('/settings/ui'), () => HttpResponse.json({ ...uiSettings, seriesSections: { related: true, similar: false } })),
    ),
  },
  play: async ({ canvas }) => {
    const similar = await canvas.findByRole('switch', { name: /^More like this/ })
    await waitFor(() => expect(similar).not.toBeChecked())
    await waitFor(() => expect(canvas.getByRole('switch', { name: /^Related series/ })).toBeChecked())
  },
}

export const HomeAndStartPage: Story = {
  render: () => <HomeSectionsSection />,
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getByRole('link', { name: /Edit Home layout/ })).toHaveAttribute('href', '/home?edit=1'))
    await waitFor(() => expect(canvas.getByRole('link', { name: /Edit Discover layout/ })).toHaveAttribute('href', '/discover?edit=1'))
  },
}

export const HomeTurnedOff: Story = {
  render: () => <HomeSectionsSection />,
  parameters: {
    msw: settingsMsw(
      http.get(api('/settings/ui'), () =>
        HttpResponse.json({ ...uiSettings, startPage: 'library', homeLayout: { ...uiSettings.homeLayout, enabled: false } }),
      ),
    ),
  },
  play: async ({ canvas }) => {
    const toggle = await canvas.findByRole('switch', { name: /^Show the Home page/ })
    await waitFor(() => expect(toggle).not.toBeChecked())
    await waitFor(() => expect(canvas.getByRole('link', { name: /Edit Home layout/ })).toHaveAttribute('data-disabled', 'true'))
  },
}

export const HomeWithoutLocalDatabase: Story = {
  render: () => <HomeSectionsSection />,
  parameters: {
    msw: settingsMsw(
      http.get(api('/settings/metadata'), () =>
        HttpResponse.json({ dumpPresent: false, dumpSizeBytes: null, dumpRefreshedAt: null, dumpDownloadFailed: false }),
      ),
    ),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('link', { name: /Edit Home layout/ })).toBeVisible()
    await waitFor(() => expect(canvas.queryByRole('link', { name: /Edit Discover layout/ })).toBeNull())
  },
}

export const Appearance: Story = {
  render: () => <AppearanceSection />,
  play: async ({ canvas, userEvent }) => {
    const group = within(await canvas.findByRole('radiogroup', { name: 'Background' }))
    const light = await group.findByRole('radio', { name: 'Light' })
    await userEvent.click(light)
    await waitFor(() => expect(light).toHaveAttribute('aria-checked', 'true'))
  },
}
