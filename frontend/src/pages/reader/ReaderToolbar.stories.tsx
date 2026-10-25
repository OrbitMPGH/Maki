import { useState, type ComponentProps } from 'react'
import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { chapters, doublePrefs, makeManifest, mangaPrefs, readingProfiles, webtoonPrefs } from '../../../.storybook/mocks/entry/fixtures'
import ReaderToolbar from './ReaderToolbar'

type ToolbarProps = ComponentProps<typeof ReaderToolbar>

// The page owns the prefs, so a toggle has to land somewhere for the control to move.
function StatefulToolbar(props: ToolbarProps) {
  const [prefs, setPrefs] = useState(props.prefs)
  const [selection, setSelection] = useState(props.selection)
  const [incognito, setIncognito] = useState(props.incognito)
  return (
    <ReaderToolbar
      {...props}
      prefs={prefs}
      onPrefs={(patch) => {
        props.onPrefs(patch)
        setPrefs((current) => ({ ...current, ...patch }))
      }}
      selection={selection}
      onSelection={(next) => {
        props.onSelection(next)
        setSelection(next)
      }}
      incognito={incognito}
      onIncognito={(value) => {
        props.onIncognito(value)
        setIncognito(value)
      }}
    />
  )
}

const meta = {
  component: ReaderToolbar,
  tags: ['ai-generated'],
  parameters: { layout: 'fullscreen' },
  args: {
    manifest: chapters[1132],
    page: 5,
    pageLabel: '6',
    onSeek: fn(),
    onPrevChapter: fn(),
    onNextChapter: fn(),
    prefs: mangaPrefs,
    onPrefs: fn(),
    selection: 'auto',
    onSelection: fn(),
    source: 'Profile',
    autoProfileId: 1,
    profiles: readingProfiles,
    fullscreen: false,
    onToggleFullscreen: fn(),
    zoom: 1,
    zoomMax: 4,
    onZoomIn: fn(),
    onZoomOut: fn(),
    onZoomReset: fn(),
    incognito: false,
    onIncognito: fn(),
    readingCounted: false,
    bookmarked: false,
    bookmarkDisabled: false,
    onToggleBookmark: fn(),
    stripOpen: false,
    onToggleStrip: fn(),
    visible: true,
    onHold: fn(),
    onReveal: fn(),
    onShortcuts: fn(),
  },
  render: (args) => <StatefulToolbar {...args} />,
  decorators: [
    (Story) => (
      <div className="reader-root">
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof ReaderToolbar>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ args, canvas, userEvent }) => {
    await expect(await canvas.findByText('Lanterns Over Kasumi')).toBeVisible()
    await expect(await canvas.findByText('131/142 chapters read · 150 in series')).toBeVisible()
    await userEvent.click(await canvas.findByRole('button', { name: 'Toggle bookmark' }))
    await expect(args.onToggleBookmark).toHaveBeenCalledOnce()
    await userEvent.click(await canvas.findByRole('button', { name: 'Keyboard shortcuts' }))
    await expect(args.onShortcuts).toHaveBeenCalledOnce()
  },
}

export const SettingsOpen: Story = {
  play: async ({ args, canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Reader settings' }))
    const body = within(canvasElement.ownerDocument.body)
    await expect(await body.findByText('Reading profile')).toBeVisible()
    await userEvent.click(await body.findByText('Continuous'))
    await waitFor(() => expect(args.onPrefs).toHaveBeenCalledWith({ mode: 'vertical' }))
    await userEvent.click(await body.findByLabelText('Incognito (don\'t record this session)'))
    await waitFor(() => expect(args.onIncognito).toHaveBeenCalledWith(true))
    // The toggle stays on after the click because the harness holds the state.
    await waitFor(() => expect(body.getByLabelText('Incognito (don\'t record this session)')).toBeChecked())
  },
}

export const ProfileDropdown: Story = {
  // The profile Select portals its options outside the settings popover, so pressing one counts as an
  // outside click; the popover's exit transition is what keeps the option mounted long enough to fire.
  parameters: { realMotion: true },
  play: async ({ args, canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Reader settings' }))
    const body = within(canvasElement.ownerDocument.body)
    await userEvent.click(await body.findByLabelText('Reading profile', { selector: 'input' }))
    await userEvent.click(await body.findByRole('option', { name: 'Night reading' }))
    await waitFor(() => expect(args.onSelection).toHaveBeenCalledWith(3))
  },
}

export const EnhanceOn: Story = {
  args: { prefs: { ...mangaPrefs, enhance: 'always', enhanceMethod: 'a4k-m' } },
  parameters: { realMotion: true },
  play: async ({ args, canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Reader settings' }))
    const body = within(canvasElement.ownerDocument.body)
    const level = await body.findByRole('radiogroup', { name: 'Enhance' })
    await waitFor(() => expect(within(level).getByLabelText('Always')).toBeChecked())
    const method = await body.findByLabelText('Method', { selector: 'input' })
    await expect(method).toHaveValue('Balanced')

    await userEvent.click(method)
    await expect(await body.findByRole('option', { name: /Light/ })).toBeVisible()
    await expect(body.getByRole('option', { name: /Sharp \(server\)/ })).not.toHaveAttribute('data-combobox-disabled', 'true')
    const downloading = body.getByRole('option', { name: /Crisp line art \(server\)/ })
    await expect(downloading).toHaveAttribute('data-combobox-disabled', 'true')
    await expect(within(downloading).getByText('Downloading')).toBeVisible()

    await userEvent.click(body.getByRole('option', { name: /Sharp \(server\)/ }))
    await waitFor(() => expect(args.onPrefs).toHaveBeenCalledWith({ enhanceMethod: 'server:span-s' }))
  },
}

export const EnhanceOffHidesMethod: Story = {
  play: async ({ args, canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Reader settings' }))
    const body = within(canvasElement.ownerDocument.body)
    const level = await body.findByRole('radiogroup', { name: 'Enhance' })
    await waitFor(() => expect(within(level).getByLabelText('Off')).toBeChecked())
    await expect(body.queryByLabelText('Method', { selector: 'input' })).toBeNull()
    await userEvent.click(within(level).getByText('Auto'))
    await waitFor(() => expect(args.onPrefs).toHaveBeenCalledWith({ enhance: 'auto' }))
    await expect(await body.findByLabelText('Method', { selector: 'input' })).toBeVisible()
  },
}

export const EnhanceSavedServerModelGone: Story = {
  args: {
    prefs: { ...mangaPrefs, enhance: 'always', enhanceMethod: 'server:realesrgan-av3' },
    manifest: makeManifest({ ...chapters[1132], enhanceServer: { enabled: false, models: [] } }),
  },
  parameters: { realMotion: true },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Reader settings' }))
    const body = within(canvasElement.ownerDocument.body)
    const method = await body.findByLabelText('Method', { selector: 'input' })
    await expect(method).toHaveValue('Crisp line art (server)')
    await userEvent.click(method)
    const gone = await body.findByRole('option', { name: /Crisp line art/ })
    await expect(gone).toHaveAttribute('data-combobox-disabled', 'true')
    await expect(within(gone).getByText('Not available')).toBeVisible()
    await expect(body.queryByRole('option', { name: /Sharp \(server\)/ })).toBeNull()
  },
}

export const SeriesOverride: Story = {
  args: { selection: 'series', source: 'Series' },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Reader settings' }))
    const body = within(canvasElement.ownerDocument.body)
    await expect(await body.findByText('Changes apply to this series only.')).toBeInTheDocument()
  },
}

export const GlobalDefaults: Story = {
  args: { selection: 'auto', source: 'Global', autoProfileId: null },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Reader settings' }))
    const body = within(canvasElement.ownerDocument.body)
    await expect(await body.findByText('Changes apply to your reader defaults.')).toBeInTheDocument()
  },
}

export const OriginalFitShowsScale: Story = {
  args: { prefs: { ...mangaPrefs, fit: 'original', scale: 150 } },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Reader settings' }))
    const body = within(canvasElement.ownerDocument.body)
    await expect(await body.findByText('Scale (150%)')).toBeInTheDocument()
  },
}

export const FirstChapterOfSeries: Story = {
  args: {
    manifest: makeManifest({ ...chapters[1132], previousChapterId: null }),
    prefs: { ...mangaPrefs, direction: 'ltr' },
  },
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Previous chapter' })).toBeDisabled())
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Next chapter' })).toBeEnabled())
  },
}

export const LastChapterOnDisk: Story = {
  args: { manifest: chapters[1142], page: 11, pageLabel: '12' },
  play: async ({ canvas }) => {
    // Right to left mirrors the chevrons, so the left one is the (missing) next chapter.
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Next chapter' })).toBeDisabled())
  },
}

export const NextChapterFromTheToolbar: Story = {
  play: async ({ args, canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Next chapter' }))
    await expect(args.onNextChapter).toHaveBeenCalledOnce()
  },
}

export const EverythingRead: Story = {
  args: { manifest: chapters[2096], readingCounted: true, page: 23, pageLabel: '24' },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('all read')).toBeVisible()
  },
}

export const Bookmarked: Story = {
  args: { bookmarked: true },
}

export const BookmarksNeedConnection: Story = {
  args: { bookmarkDisabled: true },
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Toggle bookmark' })).toBeDisabled())
  },
}

export const Incognito: Story = {
  args: { incognito: true },
}

export const ZoomedIn: Story = {
  args: { zoom: 2.5 },
  play: async ({ args, canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Reset zoom' }))
    await expect(args.onZoomReset).toHaveBeenCalledOnce()
  },
}

export const ThumbnailStripOpen: Story = {
  args: { stripOpen: true },
}

export const ContinuousModeHidesZoom: Story = {
  args: { manifest: chapters[3040], prefs: webtoonPrefs, page: 12, pageLabel: '13', autoProfileId: 2 },
  play: async ({ canvas }) => {
    await expect(canvas.queryByRole('button', { name: 'Zoom in' })).not.toBeInTheDocument()
  },
}

export const DoublePageSpread: Story = {
  args: { manifest: chapters[1201], prefs: doublePrefs, page: 3, pageLabel: '4-5', selection: 'auto', source: 'Global' },
}

export const Hidden: Story = {
  args: { visible: false },
}

export const Mobile: Story = {
  parameters: { viewport: { defaultViewport: 'mobile1' } },
  globals: { viewport: { value: 'mobile1', isRotated: false } },
}
