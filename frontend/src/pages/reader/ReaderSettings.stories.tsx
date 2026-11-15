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
    await waitFor(() => expect(b.getByRole('button', { name: 'Reading profile: Auto (Manga)' })).toBeVisible())
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
    await waitFor(() => expect(b.getByRole('radiogroup', { name: 'Layout' })).toBeVisible())
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

/** While reading holds what is on screen as you read, with the tap zones one level down. */
export const WhileReading: Story = {
  play: async ({ args, canvasElement, userEvent }) => {
    const b = body(canvasElement)
    const row = await b.findByRole('button', { name: /^While reading/ })
    const before = row.textContent
    await userEvent.click(row)
    await expect(await b.findByRole('button', { name: /^Tap zones\s*Sides$/ })).toBeVisible()
    await userEvent.click(await b.findByRole('switch', { name: /Show page number/ }))
    await waitFor(() => expect(args.onPrefs).toHaveBeenCalledWith({ showPageNumber: false }))
    await expect(b.queryByRole('switch', { name: /Skip recaps/ })).toBeNull()
    await userEvent.click(b.getByRole('button', { name: /^Back/ }))
    await waitFor(() => expect(b.getByRole('button', { name: /^While reading/ }).textContent).not.toBe(before))
  },
}

/** Chapters holds what happens as a chapter opens and ends, and says how many credit pages this one left out. */
export const Chapters: Story = {
  args: { prefs: { ...mangaPrefs, hideCredits: true }, hiddenCredits: { count: 2, cut: false, onShow: fn() } },
  play: async ({ args, canvasElement, userEvent }) => {
    const b = body(canvasElement)
    await userEvent.click(await b.findByRole('button', { name: /^Chapters/ }))
    await userEvent.click(await b.findByRole('switch', { name: /Skip recaps/ }))
    await waitFor(() => expect(args.onPrefs).toHaveBeenCalledWith({ autoSkipRecap: true }))
    const line = await b.findByText('2 pages hidden in this chapter')
    await waitFor(() => expect(line).toBeVisible())
    await userEvent.click(b.getByRole('button', { name: 'Show' }))
    await waitFor(() => expect(args.hiddenCredits?.onShow).toHaveBeenCalled())
    await expect(b.queryByRole('switch', { name: /Show page number/ })).toBeNull()
  },
}

/** A chapter that only cuts the bottom of its last page says so, without counting it as a page, and can still show it. */
export const ChaptersWithOnlyACut: Story = {
  args: { prefs: { ...webtoonPrefs, hideCredits: true }, hiddenCredits: { count: 0, cut: true, onShow: fn() } },
  play: async ({ args, canvasElement, userEvent }) => {
    const b = body(canvasElement)
    await userEvent.click(await b.findByRole('button', { name: /^Chapters/ }))
    const line = await b.findByText('End of the last page hidden in this chapter')
    await waitFor(() => expect(line).toBeVisible())
    await userEvent.click(b.getByRole('button', { name: 'Show' }))
    await waitFor(() => expect(args.hiddenCredits?.onShow).toHaveBeenCalled())
  },
}

/** Hidden pages and a cut are named together. */
export const ChaptersWithPagesAndACut: Story = {
  args: { prefs: { ...webtoonPrefs, hideCredits: true }, hiddenCredits: { count: 1, cut: true, onShow: fn() } },
  play: async ({ canvasElement, userEvent }) => {
    const b = body(canvasElement)
    await userEvent.click(await b.findByRole('button', { name: /^Chapters/ }))
    await expect(await b.findByText('1 page and the end of the last page hidden in this chapter')).toBeInTheDocument()
  },
}

/** Opens While reading, then its Tap zones page. */
async function openTapZones(b: ReturnType<typeof body>, userEvent: Parameters<NonNullable<Story['play']>>[0]['userEvent']) {
  await userEvent.click(await b.findByRole('button', { name: /^While reading/ }))
  await userEvent.click(await b.findByRole('button', { name: /^Tap zones/ }))
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

export const PageLookAuto: Story = {
  play: async ({ args, canvasElement, userEvent }) => {
    const b = body(canvasElement)
    await userEvent.click(await b.findByRole('button', { name: /^Page look\s*Dark$/ }))
    const group = await b.findByRole('radiogroup', { name: 'Background' })
    await userEvent.click(within(group).getByText('White'))
    await waitFor(() => expect(args.onPrefs).toHaveBeenCalledWith({ background: '#ffffff' }))
    await userEvent.click(within(group).getByText('Grey'))
    await waitFor(() => expect(args.onPrefs).toHaveBeenCalledWith({ background: '#8c8c90' }))
    await userEvent.click(within(group).getByText('Auto'))
    await waitFor(() => expect(args.onPrefs).toHaveBeenCalledWith({ background: 'auto' }))
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
    await waitFor(() => expect(b.getByRole('radiogroup', { name: 'Layout' })).toBeVisible())
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
    await waitFor(() => expect(b.getByRole('menuitemradio', { name: 'Night reading' })).toBeVisible())
    await userEvent.keyboard('{Escape}')
    await waitFor(() => expect(b.queryByRole('menuitemradio', { name: 'Night reading' })).toBeNull())
    await waitFor(() => expect(b.getByRole('radiogroup', { name: 'Layout' })).toBeVisible())
    await expect(args.onOpenedChange).not.toHaveBeenCalled()
  },
}

export const SeriesOverride: Story = {
  args: { selection: 'series', source: 'Series' },
  play: async ({ canvasElement }) => {
    await waitFor(() => expect(body(canvasElement).getByRole('button', { name: 'Reading profile: Just this series' })).toBeVisible())
    await waitFor(() => expect(canvasElement.ownerDocument.querySelector('.reader-settings-scope')).toHaveTextContent('Changes apply to this series only.'))
  },
}

export const GlobalDefaults: Story = {
  args: { selection: 'auto', source: 'Global', autoProfileId: null },
  play: async ({ canvasElement }) => {
    await waitFor(() => expect(body(canvasElement).getByRole('button', { name: 'Reading profile: Auto (my defaults)' })).toBeVisible())
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
    await expect(b.queryByRole('radiogroup', { name: 'While the server works' })).toBeNull()

    await userEvent.click(await b.findByRole('button', { name: /^Method\s*Balanced$/ }))
    const light = await b.findByRole('radio', { name: /^Light/ })
    await waitFor(() => expect(light).toBeVisible())
    await waitFor(() => expect(b.getByRole('radio', { name: /^Balanced/ })).toHaveAttribute('aria-checked', 'true'))
    await waitFor(() => expect(b.getByRole('radio', { name: /SPAN-S/ })).toBeEnabled())
    const downloading = b.getByRole('radio', { name: /Real-ESRGAN/ })
    await waitFor(() => expect(downloading).toBeDisabled())
    await waitFor(() => expect(within(downloading).getByText('Downloading')).toBeVisible())

    await userEvent.click(b.getByRole('radio', { name: /SPAN-S/ }))
    await waitFor(() => expect(args.onPrefs).toHaveBeenCalledWith({ enhanceMethod: 'server:span-s' }))
    // Picking one steps back to the Enhance page, focus on the row that opened the list.
    await waitFor(() => expect(b.getByRole('button', { name: /^Method/ })).toHaveFocus())
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
    await expect(b.queryByRole('button', { name: /^Method/ })).toBeNull()
    await userEvent.click(within(level).getByText('Auto'))
    await waitFor(() => expect(args.onPrefs).toHaveBeenCalledWith({ enhance: 'auto' }))
    await waitFor(() => expect(b.getByRole('button', { name: /^Method/ })).toBeVisible())
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
    await waitFor(() => expect(b.getByText('Off for this chapter because of its quality.')).toBeVisible())
    await userEvent.click(await b.findByRole('button', { name: /^Don't enhance/ }))
    await userEvent.click(await b.findByRole('checkbox', { name: 'Volume' }))
    await waitFor(() => expect(args.onPrefs).toHaveBeenCalledWith({ enhanceSkipTiers: [] }))
    await userEvent.click(b.getByRole('button', { name: /^Back/ }))
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
    await userEvent.click(await b.findByRole('button', { name: /^Method\s*Real-ESRGAN$/ }))
    const gone = await b.findByRole('radio', { name: /Real-ESRGAN/ })
    await waitFor(() => expect(gone).toBeDisabled())
    await waitFor(() => expect(within(gone).getByText('Not available')).toBeVisible())
    await expect(b.queryByRole('radio', { name: /SPAN-S/ })).toBeNull()
  },
}

export const OriginalFitShowsScale: Story = {
  args: { prefs: { ...mangaPrefs, fit: 'original', scale: 150 } },
  play: async ({ canvasElement }) => {
    await waitFor(() => expect(body(canvasElement).getByText('Scale (150%)')).toBeVisible())
  },
}

export const ContinuousPickedByDetection: Story = {
  args: { prefs: webtoonPrefs, autoLayout: true, manifest: chapters[3040], autoProfileId: 2 },
  play: async ({ canvasElement }) => {
    const b = body(canvasElement)
    await waitFor(() => expect(b.getByText("Continuous, because this chapter's pages are very tall.")).toBeVisible())
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

/** With tap zones on, the Tap zones page picks the layout, the zone size and the swap, and draws the zones. */
export const TapZoneLayouts: Story = {
  args: { onTapZonesShown: fn() },
  play: async ({ args, canvasElement, userEvent }) => {
    const b = body(canvasElement)
    await userEvent.click(await b.findByRole('button', { name: /^While reading/ }))
    await b.findByRole('button', { name: /^Tap zones/ })
    await expect(args.onTapZonesShown).not.toHaveBeenCalledWith(true)
    await userEvent.click(b.getByRole('button', { name: /^Tap zones/ }))
    await waitFor(() => expect(args.onTapZonesShown).toHaveBeenLastCalledWith(true))
    await expect(canvasElement.ownerDocument.querySelector('.reader-tap-preview')).toBeNull()
    await userEvent.click(within(await b.findByRole('radiogroup', { name: 'Tap zone layout' })).getByText('Kindle'))
    await waitFor(() => expect(args.onPrefs).toHaveBeenCalledWith({ tapLayout: 'kindle' }))
    await waitFor(() => expect(b.getByText(/Most of the page goes forward/)).toBeVisible())
    await userEvent.click(b.getByRole('switch', { name: /Swap back and forward/ }))
    await waitFor(() => expect(args.onPrefs).toHaveBeenCalledWith({ tapInvert: true }))
    await userEvent.click(b.getByRole('switch', { name: /Tap zones/ }))
    await waitFor(() => expect(b.queryByRole('radiogroup', { name: 'Tap zone layout' })).toBeNull())
    await waitFor(() => expect(args.onTapZonesShown).toHaveBeenLastCalledWith(false))
    await userEvent.click(b.getByRole('button', { name: /^Back/ }))
    await expect(await b.findByRole('button', { name: /^Tap zones\s*Off$/ })).toBeVisible()
  },
}

/** In Continuous the layouts don't apply: the picker is off and the zone size sets the top and bottom bands. */
export const TapZonesContinuous: Story = {
  args: { prefs: { ...mangaPrefs, mode: 'vertical', tapZones: true } },
  play: async ({ canvasElement, userEvent }) => {
    const b = body(canvasElement)
    await openTapZones(b, userEvent)
    await waitFor(() => expect(b.getByText(/a band at the top goes back/)).toBeVisible())
    await waitFor(() => expect(b.getByRole('radio', { name: 'Kindle' })).toBeDisabled())
    await waitFor(() => expect(b.getByRole('slider', { name: 'Zone size' })).toBeEnabled())
  },
}
