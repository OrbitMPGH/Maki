import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api } from '../../.storybook/mocks/api'
import { settingsMsw } from '../../.storybook/mocks/settings'
import { pageConversionIdle, pageConversionRunning } from '../../.storybook/mocks/settings/data'
import { keptPreviewHandler } from '../../.storybook/mocks/settings/pageConversion'
import PageConversionWizardPage from './PageConversionWizardPage'

const meta = {
  component: PageConversionWizardPage,
  tags: ['ai-generated'],
  parameters: {
    layout: 'fullscreen',
    router: { path: '/settings/page-conversion', initialEntries: ['/settings/page-conversion'] },
    msw: settingsMsw(),
  },
} satisfies Meta<typeof PageConversionWizardPage>

export default meta
type Story = StoryObj<typeof meta>

const atStep = (step: number) => ({
  router: { path: '/settings/page-conversion', initialEntries: [`/settings/page-conversion?step=${step}`] },
})

const withSettings = (settings: { format: 'webp' | 'avif' | null; stop: string; pngLossless: boolean }) =>
  settingsMsw(http.get(api('/pageconversion'), () => HttpResponse.json({ ...pageConversionIdle, settings })))

let starts = 0

const body = (canvasElement: HTMLElement) => within(canvasElement.ownerDocument.body)

export const Format: Story = {
  play: async ({ canvas, userEvent }) => {
    await waitFor(() => expect(canvas.getByRole('radio', { name: /WebP/ })).toBeChecked())
    await expect(canvas.getByText(/KOReader and some e-readers can't open AVIF pages/)).toBeVisible()
    await waitFor(() => expect(canvas.getByRole('checkbox', { name: 'Keep PNG pages lossless' })).toBeChecked())

    await userEvent.click(canvas.getByRole('radio', { name: /AVIF/ }))
    await expect(canvas.getByRole('radio', { name: /AVIF/ })).toBeChecked()

    await userEvent.click(canvas.getByRole('radio', { name: /Off/ }))
    await expect(canvas.queryByRole('checkbox', { name: 'Keep PNG pages lossless' })).toBeNull()
    await expect(canvas.getByText(/With conversion off/)).toBeVisible()
  },
}

export const FormatOffThenCompare: Story = {
  parameters: { ...atStep(1), msw: withSettings({ format: null, stop: 'high', pngLossless: false }) },
  play: async ({ canvas, userEvent }) => {
    await waitFor(() => expect(canvas.getByRole('radio', { name: /WebP/ })).toBeChecked())
    await expect(canvas.getByRole('checkbox', { name: 'Keep PNG pages lossless' })).not.toBeChecked()
    await userEvent.click(canvas.getByRole('radio', { name: /Off/ }))
    await userEvent.click(canvas.getByRole('button', { name: 'Next' }))
    await expect(await canvas.findByText(/Conversion is off, so there is nothing to compare/)).toBeVisible()
  },
}

export const Compare: Story = {
  parameters: atStep(2),
  play: async ({ canvas, userEvent }) => {
    await expect(await canvas.findByRole('button', { name: /Lanterns Over Kasumi/ })).toBeVisible()
    await expect(canvas.getByRole('button', { name: /Ashen Crown Regression.*Colour page/ })).toBeVisible()
    await expect(canvas.getByRole('button', { name: /Iron Petal Academy.*Heaviest kind of file/ })).toBeVisible()
    await expect(canvas.getByText('Page 15 of 38')).toBeVisible()

    await expect(await canvas.findByRole('img', { name: 'Original page' })).toBeVisible()
    await expect(await canvas.findByRole('img', { name: 'Converted page' })).toBeInTheDocument()
    await expect(canvas.getByRole('slider', { name: 'Comparison divider' })).toBeVisible()

    await waitFor(() => expect(canvas.getByText(/% of the original/)).toBeVisible())
    await expect(canvas.getByText('Tone is a little lighter. Fine at fit-to-screen.')).toBeVisible()

    canvas.getByRole('slider', { name: 'Quality' }).focus()
    await userEvent.keyboard('{End}')
    await expect(await canvas.findByText('Identical pixels.')).toBeVisible()
    await expect(await canvas.findByText('JPEG pages are kept as they are at Lossless.')).toBeVisible()

    await userEvent.keyboard('{Home}')
    await expect(await canvas.findByText('Screentone is smoothed away, lines stay intact.')).toBeVisible()
    await waitFor(() => expect(canvas.getByText('448 KB, 37% of the original')).toBeVisible())
  },
}

export const CompareKeptNotSmaller: Story = {
  parameters: { ...atStep(2), msw: settingsMsw(keptPreviewHandler('notsmaller')) },
  play: async ({ canvas }) => {
    await expect(
      await canvas.findByText('Converting would not make this page at least 5% smaller, so the pass keeps it as it is.'),
    ).toBeVisible()
  },
}

export const CompareKeptUnknownReason: Story = {
  parameters: { ...atStep(2), msw: settingsMsw(keptPreviewHandler('somethingnew')) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('This page is kept as it is.')).toBeVisible()
  },
}

export const CompareLoupeAndDivider: Story = {
  parameters: atStep(2),
  play: async ({ canvas, userEvent }) => {
    const loupe = await canvas.findByRole('button', { name: 'Loupe, 2x' })
    await expect(loupe).toHaveAttribute('aria-pressed', 'false')
    await userEvent.click(loupe)
    await expect(loupe).toHaveAttribute('aria-pressed', 'true')

    const divider = await canvas.findByRole('slider', { name: 'Comparison divider' })
    await expect(divider).toHaveAttribute('aria-valuenow', '50')
    divider.focus()
    await userEvent.keyboard('{ArrowRight}{ArrowRight}')
    await expect(divider).toHaveAttribute('aria-valuenow', '54')
    await userEvent.keyboard('{Home}')
    await expect(divider).toHaveAttribute('aria-valuenow', '0')
  },
}

export const CompareAvif: Story = {
  parameters: { ...atStep(2), msw: withSettings({ format: 'avif', stop: 'high', pngLossless: false }) },
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getByText('751 KB, 62% of the original')).toBeVisible())
  },
}

export const CompareWithoutSuggestions: Story = {
  parameters: {
    ...atStep(2),
    msw: settingsMsw(http.get(api('/pageconversion/samples'), () => HttpResponse.json({ samples: [] }))),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/Maki found no suitable pages to suggest/)).toBeVisible()
    await expect(canvas.getByRole('button', { name: 'Pick another page' })).toBeEnabled()
  },
}

export const PickAnotherPage: Story = {
  parameters: atStep(2),
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Pick another page' }))
    const dialog = await body(canvasElement).findByRole('dialog', { name: 'Pick another page' })

    await userEvent.type(within(dialog).getByRole('textbox', { name: 'Search your series' }), 'Ninth')
    await userEvent.click(await within(dialog).findByRole('button', { name: 'The Ninth Kitchen' }))
    await userEvent.click(await within(dialog).findByRole('button', { name: /Ch\.3(?!\d)/ }))

    const page = await within(dialog).findByRole('textbox', { name: 'Page' })
    await waitFor(() => expect(page).toHaveValue('12'))
    await expect(within(dialog).getByText(/Pages 1 to 32/)).toBeVisible()
    await userEvent.clear(page)
    await userEvent.type(page, '20')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Compare this page' }))

    const custom = await canvas.findByRole('button', { name: /The Ninth Kitchen.*Your pick/ })
    await expect(custom).toHaveAttribute('aria-pressed', 'true')
    await expect(canvas.getByText('Page 20 of 32')).toBeVisible()
  },
}

export const Start: Story = {
  parameters: atStep(3),
  play: async ({ canvas, canvasElement, userEvent }) => {
    await expect(await canvas.findByText(/Balanced\. Tone is a little lighter/)).toBeVisible()
    await expect(canvas.getByText('Kept lossless')).toBeVisible()
    await expect(canvas.getByRole('button', { name: 'Save and start' })).toBeEnabled()

    await userEvent.click(canvas.getByRole('button', { name: 'Save only' }))
    await expect(await body(canvasElement).findByText('Saved')).toBeVisible()
  },
}

export const StartWithConversionOff: Story = {
  parameters: atStep(1),
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('radio', { name: /Off/ }))
    await userEvent.click(canvas.getByRole('button', { name: 'Next' }))
    await userEvent.click(await canvas.findByRole('button', { name: 'Next' }))
    await expect(await canvas.findByRole('button', { name: 'Save only' })).toBeEnabled()
    await expect(canvas.queryByRole('button', { name: 'Save and start' })).toBeNull()
  },
}

export const StartWhilePassRuns: Story = {
  parameters: {
    router: { path: '/settings/page-conversion', initialEntries: ['/settings/page-conversion?step=1'] },
    msw: settingsMsw(http.get(api('/pageconversion'), () => HttpResponse.json(pageConversionRunning))),
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await waitFor(() => expect(canvas.getByRole('radio', { name: /AVIF/ })).toBeChecked())
    await userEvent.click(canvas.getByRole('radio', { name: /WebP/ }))
    await userEvent.click(canvas.getByRole('button', { name: 'Next' }))
    await userEvent.click(await canvas.findByRole('button', { name: 'Next' }))
    await userEvent.click(await canvas.findByRole('button', { name: 'Save and start' }))

    const dialog = await body(canvasElement).findByRole('dialog')
    await expect(within(dialog).getByText('Pause the running pass and restart it with the new settings?')).toBeVisible()
    await expect(within(dialog).getByRole('button', { name: 'Restart' })).toBeVisible()
    await expect(within(dialog).getByRole('button', { name: 'Save and pause' })).toBeVisible()
  },
}

export const SaveOnlyWhilePassRunsThenRestart: Story = {
  parameters: {
    router: { path: '/settings/page-conversion', initialEntries: ['/settings/page-conversion?step=1'] },
    msw: settingsMsw(
      http.get(api('/pageconversion'), () => HttpResponse.json(pageConversionRunning)),
      http.post(api('/pageconversion/start'), () => {
        starts += 1
        return HttpResponse.json(pageConversionRunning)
      }),
    ),
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    starts = 0
    await waitFor(() => expect(canvas.getByRole('radio', { name: /AVIF/ })).toBeChecked())
    await userEvent.click(canvas.getByRole('radio', { name: /WebP/ }))
    await userEvent.click(canvas.getByRole('button', { name: 'Next' }))
    await userEvent.click(await canvas.findByRole('button', { name: 'Next' }))
    await userEvent.click(await canvas.findByRole('button', { name: 'Save only' }))

    const dialog = await body(canvasElement).findByRole('dialog')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Restart' }))
    await waitFor(() => expect(starts).toBe(1))
  },
}
