import { useState, type ComponentProps } from 'react'
import { Group } from '@mantine/core'
import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { pageImage } from '../../../.storybook/mocks/api'
import { chapters, makeManifest, mangaPrefs, readingProfiles, webtoonPrefs } from '../../../.storybook/mocks/entry/fixtures'
import ReaderSettings from './ReaderSettings'

type SettingsProps = ComponentProps<typeof ReaderSettings>

// The reader owns the prefs and the open state, so a change has to land somewhere for the control to move.
function StatefulSettings(props: SettingsProps) {
  const [opened, setOpened] = useState(props.opened)
  const [prefs, setPrefs] = useState(props.prefs)
  const [selection, setSelection] = useState(props.selection)
  const [incognito, setIncognito] = useState(props.incognito)
  return (
    <ReaderSettings
      {...props}
      opened={opened}
      onOpenedChange={(next) => {
        props.onOpenedChange(next)
        setOpened(next)
      }}
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
  component: ReaderSettings,
  tags: ['ai-generated'],
  parameters: { layout: 'fullscreen' },
  args: {
    opened: true,
    onOpenedChange: fn(),
    manifest: chapters[1132],
    prefs: mangaPrefs,
    onPrefs: fn(),
    autoLayout: false,
    selection: 'auto',
    onSelection: fn(),
    source: 'Profile',
    autoProfileId: 1,
    profiles: readingProfiles,
    incognito: false,
    onIncognito: fn(),
    previewSrc: pageImage(1132, 6),
  },
  render: (args) => <StatefulSettings {...args} />,
  decorators: [
    (Story) => (
      <div className="reader-root">
        <div className="reader-bar reader-bar-bottom" data-visible="true">
          <Group justify="flex-end" px="md" h="100%">
            <Story />
          </Group>
        </div>
      </div>
    ),
  ],
} satisfies Meta<typeof ReaderSettings>

export default meta
type Story = StoryObj<typeof meta>

const body = (canvasElement: HTMLElement) => within(canvasElement.ownerDocument.body)

export const Default: Story = {
  play: async ({ args, canvasElement, userEvent }) => {
    const b = body(canvasElement)
    await expect(await b.findByRole('button', { name: 'Reading profile: Auto (Manga)' })).toBeVisible()
    await userEvent.click(b.getByRole('radio', { name: 'Double' }))
    await waitFor(() => expect(args.onPrefs).toHaveBeenCalledWith({ mode: 'double' }))
    await waitFor(() => expect(b.getByRole('radio', { name: 'Double' })).toBeChecked())
    // Wide pages only applies to the Single layout, so its row goes with it.
    await expect(b.queryByRole('button', { name: /^Wide pages/ })).toBeNull()
    await userEvent.click(b.getByRole('radio', { name: 'Sepia' }))
    await waitFor(() => expect(args.onPrefs).toHaveBeenCalledWith({ filter: 'sepia' }))
    await userEvent.click(b.getByRole('radio', { name: 'Left to right' }))
    await waitFor(() => expect(args.onPrefs).toHaveBeenCalledWith({ direction: 'ltr' }))
    await userEvent.click(b.getByRole('radio', { name: 'Fit width' }))
    await waitFor(() => expect(args.onPrefs).toHaveBeenCalledWith({ fit: 'width' }))
  },
}

export const IncognitoToggle: Story = {
  play: async ({ args, canvasElement, userEvent }) => {
    const b = body(canvasElement)
    const toggle = await b.findByRole('button', { name: 'Incognito' })
    await waitFor(() => expect(toggle).toHaveAttribute('aria-pressed', 'false'))
    await userEvent.click(toggle)
    await waitFor(() => expect(args.onIncognito).toHaveBeenCalledWith(true))
    await waitFor(() => expect(b.getByRole('button', { name: 'Incognito' })).toHaveAttribute('aria-pressed', 'true'))
  },
}

export const OpensFromTheGear: Story = {
  args: { opened: false },
  play: async ({ args, canvas, canvasElement, userEvent }) => {
    const b = body(canvasElement)
    await expect(b.queryByRole('radiogroup', { name: 'Layout' })).toBeNull()
    await userEvent.click(await canvas.findByRole('button', { name: 'Reader settings' }))
    await waitFor(() => expect(args.onOpenedChange).toHaveBeenCalledWith(true))
    await expect(await b.findByRole('radiogroup', { name: 'Layout' })).toBeVisible()
  },
}

export const EscapeStepsBackThenCloses: Story = {
  play: async ({ args, canvasElement, userEvent }) => {
    const b = body(canvasElement)
    await userEvent.click(await b.findByRole('button', { name: /^While reading/ }))
    await b.findByRole('button', { name: /^Back/ })
    await waitFor(() => expect(b.getByRole('button', { name: /^Back/ })).toHaveFocus())
    await userEvent.keyboard('{Escape}')
    await b.findByRole('radiogroup', { name: 'Layout' })
    await waitFor(() => expect(b.getByRole('radiogroup', { name: 'Layout' })).toBeVisible())
    await waitFor(() => expect(b.getByRole('button', { name: /^While reading/ })).toHaveFocus())
    await expect(args.onOpenedChange).not.toHaveBeenCalled()
    await userEvent.keyboard('{Escape}')
    await waitFor(() => expect(args.onOpenedChange).toHaveBeenCalledWith(false))
  },
}

export const WhileReading: Story = {
  play: async ({ args, canvasElement, userEvent }) => {
    const b = body(canvasElement)
    const row = await b.findByRole('button', { name: /^While reading/ })
    const before = row.textContent
    await userEvent.click(row)
    await userEvent.click(await b.findByRole('switch', { name: /Tap zones/ }))
    await waitFor(() => expect(args.onPrefs).toHaveBeenCalledWith({ tapZones: false }))
    await userEvent.click(b.getByRole('button', { name: /^Back/ }))
    await waitFor(() => expect(b.getByRole('button', { name: /^While reading/ }).textContent).not.toBe(before))
  },
}

export const PageLook: Story = {
  play: async ({ args, canvasElement, userEvent }) => {
    const b = body(canvasElement)
    await userEvent.click(await b.findByRole('button', { name: /^Page look\s*Dark$/ }))
    await userEvent.click(within(await b.findByRole('radiogroup', { name: 'Background' })).getByText('OLED black'))
    await waitFor(() => expect(args.onPrefs).toHaveBeenCalledWith({ background: '#000000' }))
    await userEvent.click(b.getByRole('switch', { name: /Crop borders/ }))
    await waitFor(() => expect(args.onPrefs).toHaveBeenCalledWith({ cropBorders: true }))
    await userEvent.click(b.getByRole('button', { name: /^Back/ }))
    await waitFor(() => expect(b.getByRole('button', { name: /^Page look\s*OLED black, cropped$/ })).toBeVisible())
  },
}

export const WidePages: Story = {
  play: async ({ args, canvasElement, userEvent }) => {
    const b = body(canvasElement)
    await userEvent.click(await b.findByRole('button', { name: /^Wide pages\s*Whole$/ }))
    await userEvent.click(within(await b.findByRole('radiogroup', { name: 'Wide pages' })).getByText('Split'))
    await waitFor(() => expect(args.onPrefs).toHaveBeenCalledWith({ splitWide: 'split' }))
  },
}

export const ProfileDropdown: Story = {
  play: async ({ args, canvasElement, userEvent }) => {
    const b = body(canvasElement)
    // Which settings a change edits is said on the first view, before anything is opened.
    await waitFor(() => expect(canvasElement.ownerDocument.querySelector('.reader-settings-scope')).toHaveTextContent('Changes apply to the Manga profile and every series using it.'))
    await userEvent.click(await b.findByRole('button', { name: 'Reading profile: Auto (Manga)' }))
    // The list drops from the chip; the panel stays on its first page behind it.
    await expect(b.getByRole('radiogroup', { name: 'Layout' })).toBeVisible()
    await userEvent.click(await b.findByRole('menuitemradio', { name: 'Night reading' }))
    await waitFor(() => expect(args.onSelection).toHaveBeenCalledWith(3))
    await waitFor(() => expect(b.queryByRole('menuitemradio', { name: 'Night reading' })).toBeNull())
    await expect(args.onOpenedChange).not.toHaveBeenCalled()
  },
}

export const EscapeClosesTheProfileListFirst: Story = {
  play: async ({ args, canvasElement, userEvent }) => {
    const b = body(canvasElement)
    await userEvent.click(await b.findByRole('button', { name: 'Reading profile: Auto (Manga)' }))
    await expect(await b.findByRole('menuitemradio', { name: 'Night reading' })).toBeVisible()
    await userEvent.keyboard('{Escape}')
    await waitFor(() => expect(b.queryByRole('menuitemradio', { name: 'Night reading' })).toBeNull())
    await expect(b.getByRole('radiogroup', { name: 'Layout' })).toBeVisible()
    await expect(args.onOpenedChange).not.toHaveBeenCalled()
  },
}

export const SeriesOverride: Story = {
  args: { selection: 'series', source: 'Series' },
  play: async ({ canvasElement }) => {
    await expect(await body(canvasElement).findByRole('button', { name: 'Reading profile: Just this series' })).toBeVisible()
    await waitFor(() => expect(canvasElement.ownerDocument.querySelector('.reader-settings-scope')).toHaveTextContent('Changes apply to this series only.'))
  },
}

export const GlobalDefaults: Story = {
  args: { selection: 'auto', source: 'Global', autoProfileId: null },
  play: async ({ canvasElement }) => {
    await expect(await body(canvasElement).findByRole('button', { name: 'Reading profile: Auto (my defaults)' })).toBeVisible()
    await waitFor(() => expect(canvasElement.ownerDocument.querySelector('.reader-settings-scope')).toHaveTextContent('Changes apply to your reader defaults.'))
  },
}

export const EnhanceOn: Story = {
  args: { prefs: { ...mangaPrefs, enhance: 'always', enhanceMethod: 'a4k-m' } },
  parameters: { realMotion: true },
  play: async ({ args, canvasElement, userEvent }) => {
    const b = body(canvasElement)
    await userEvent.click(await b.findByRole('button', { name: /^Enhance\s*Always, Balanced$/ }))
    const level = await b.findByRole('radiogroup', { name: 'Enhance' })
    await waitFor(() => expect(within(level).getByLabelText('Always')).toBeChecked())
    const method = await b.findByLabelText('Method', { selector: 'input' })
    await expect(method).toHaveValue('Balanced')
    await expect(b.queryByRole('radiogroup', { name: 'While the server works' })).toBeNull()

    await userEvent.click(method)
    await expect(await b.findByRole('option', { name: /^Light/ })).toBeVisible()
    await expect(b.getByRole('option', { name: /SPAN-S/ })).not.toHaveAttribute('data-combobox-disabled', 'true')
    const downloading = b.getByRole('option', { name: /Real-ESRGAN/ })
    await expect(downloading).toHaveAttribute('data-combobox-disabled', 'true')
    await expect(within(downloading).getByText('Downloading')).toBeVisible()

    await userEvent.click(b.getByRole('option', { name: /SPAN-S/ }))
    await waitFor(() => expect(args.onPrefs).toHaveBeenCalledWith({ enhanceMethod: 'server:span-s' }))
  },
}

export const EnhanceServerFallback: Story = {
  args: { prefs: { ...mangaPrefs, enhance: 'always', enhanceMethod: 'server:span-s', enhanceFallback: 'a4k-m' } },
  play: async ({ args, canvasElement, userEvent }) => {
    const b = body(canvasElement)
    await userEvent.click(await b.findByRole('button', { name: /^Enhance\s*Always, SPAN-S$/ }))
    const fallback = await b.findByRole('radiogroup', { name: 'While the server works' })
    await waitFor(() => expect(within(fallback).getByLabelText('Balanced')).toBeChecked())
    await userEvent.click(within(fallback).getByText('Strong'))
    await waitFor(() => expect(args.onPrefs).toHaveBeenCalledWith({ enhanceFallback: 'a4k-a' }))
    await userEvent.click(within(fallback).getByText('None'))
    await waitFor(() => expect(args.onPrefs).toHaveBeenCalledWith({ enhanceFallback: 'off' }))
    const progress = await b.findByRole('radiogroup', { name: 'Upscale progress' })
    await waitFor(() => expect(within(progress).getByLabelText('Line')).toBeChecked())
    await userEvent.click(within(progress).getByText('Hidden'))
    await waitFor(() => expect(args.onPrefs).toHaveBeenCalledWith({ enhanceProgress: 'off' }))
  },
}

export const EnhanceOffHidesMethod: Story = {
  play: async ({ args, canvasElement, userEvent }) => {
    const b = body(canvasElement)
    await userEvent.click(await b.findByRole('button', { name: /^Enhance\s*Off$/ }))
    const level = await b.findByRole('radiogroup', { name: 'Enhance' })
    await waitFor(() => expect(within(level).getByLabelText('Off')).toBeChecked())
    await expect(b.queryByLabelText('Method', { selector: 'input' })).toBeNull()
    await userEvent.click(within(level).getByText('Auto'))
    await waitFor(() => expect(args.onPrefs).toHaveBeenCalledWith({ enhance: 'auto' }))
    await expect(await b.findByLabelText('Method', { selector: 'input' })).toBeVisible()
  },
}

export const EnhanceSkippedForVolume: Story = {
  args: {
    manifest: { ...chapters[1132], tier: 'volume' },
    prefs: { ...mangaPrefs, enhance: 'always' },
  },
  play: async ({ args, canvasElement, userEvent }) => {
    const b = body(canvasElement)
    await userEvent.click(await b.findByRole('button', { name: /^Enhance\s*Off for this chapter$/ }))
    await expect(await b.findByText('Off for this chapter because of its quality.')).toBeVisible()
    await userEvent.click(await b.findByLabelText("Don't enhance", { selector: 'input' }))
    await userEvent.click(await b.findByRole('option', { name: 'Volume' }))
    await waitFor(() => expect(args.onPrefs).toHaveBeenCalledWith({ enhanceSkipTiers: [] }))
    await waitFor(() => expect(b.queryByText('Off for this chapter because of its quality.')).toBeNull())
  },
}

export const EnhanceSavedServerModelGone: Story = {
  args: {
    prefs: { ...mangaPrefs, enhance: 'always', enhanceMethod: 'server:realesrgan-av3' },
    manifest: makeManifest({ ...chapters[1132], enhanceServer: { enabled: false, models: [] } }),
  },
  parameters: { realMotion: true },
  play: async ({ canvasElement, userEvent }) => {
    const b = body(canvasElement)
    await userEvent.click(await b.findByRole('button', { name: /^Enhance/ }))
    const method = await b.findByLabelText('Method', { selector: 'input' })
    await expect(method).toHaveValue('Real-ESRGAN')
    await userEvent.click(method)
    const gone = await b.findByRole('option', { name: /Real-ESRGAN/ })
    await expect(gone).toHaveAttribute('data-combobox-disabled', 'true')
    await expect(within(gone).getByText('Not available')).toBeVisible()
    await expect(b.queryByRole('option', { name: /SPAN-S/ })).toBeNull()
  },
}

export const OriginalFitShowsScale: Story = {
  args: { prefs: { ...mangaPrefs, fit: 'original', scale: 150 } },
  play: async ({ canvasElement }) => {
    await expect(await body(canvasElement).findByText('Scale (150%)')).toBeVisible()
  },
}

export const ContinuousPickedByDetection: Story = {
  args: { prefs: webtoonPrefs, autoLayout: true, manifest: chapters[3040], autoProfileId: 2 },
  play: async ({ canvasElement }) => {
    const b = body(canvasElement)
    await expect(await b.findByText("Continuous, because this chapter's pages are very tall.")).toBeVisible()
    await waitFor(() => expect(b.getByRole('radio', { name: 'Continuous' })).toBeChecked())
  },
}

export const DimmedNoPreview: Story = {
  args: { prefs: { ...mangaPrefs, dim: 30, filter: 'grayscale' }, previewSrc: undefined },
  play: async ({ canvasElement }) => {
    const b = body(canvasElement)
    await waitFor(() => expect(b.getByRole('slider', { name: 'Brightness' })).toHaveAttribute('aria-valuenow', '70'))
    await waitFor(() => expect(b.getByRole('radio', { name: 'Gray' })).toBeChecked())
  },
}

export const Phone: Story = {
  parameters: { viewport: { defaultViewport: 'mobile1' } },
  globals: { viewport: { value: 'mobile1', isRotated: false } },
}
