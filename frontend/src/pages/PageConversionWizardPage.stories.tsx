import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { delay, http, HttpResponse } from 'msw'
import { api } from '../../.storybook/mocks/api'
import { settingsMsw } from '../../.storybook/mocks/settings'
import { pageConversionIdle, pageConversionRunning } from '../../.storybook/mocks/settings/data'
import {
  estimateFailed,
  estimateIdle,
  estimateRunning,
  keptPreviewHandler,
  pageConversionRules,
  pageConversionScope,
} from '../../.storybook/mocks/settings/pageConversion'
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

type StorySettings = {
  format: 'webp' | 'avif' | null
  stop: string
  pngLossless: boolean
  splitHeight?: number | null
  colourFormat?: 'webp' | 'avif' | null
  colourStop?: string | null
}

const settingsHandler = (settings: StorySettings) =>
  http.get(api('/pageconversion'), () => HttpResponse.json({ ...pageConversionIdle, settings }))

/** WebP Balanced for black-and-white pages, AVIF High for colour ones. */
const colourAvif: StorySettings = { format: 'webp', stop: 'balanced', pngLossless: true, colourFormat: 'avif', colourStop: 'high' }

const body = (canvasElement: HTMLElement) => within(canvasElement.ownerDocument.body)

const chip = (canvas: ReturnType<typeof within>, name: string) => canvas.getByRole('button', { name })

const noRules = http.get(api('/pageconversion/rules'), () => HttpResponse.json({ rules: [] }))

let saved: { settings: StorySettings; rules: Array<Record<string, unknown>> } | null = null
let starts = 0

const recordSave = http.put(api('/pageconversion/plan'), async ({ request }) => {
  saved = (await request.json()) as NonNullable<typeof saved>
  return HttpResponse.json({ ...pageConversionIdle, settings: saved.settings })
})

const recordStart = http.post(api('/pageconversion/start'), () => {
  starts += 1
  return HttpResponse.json(pageConversionRunning)
})

export const Plan: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('table', { name: 'Conversion plan' })).toBeVisible()
    await expect(canvas.getByText('1. Leave scraped chapters alone')).toBeVisible()
    await expect(canvas.getByText('Skipped, left as they are')).toBeVisible()
    await expect(canvas.getByText('2. Squeeze volumes harder')).toBeVisible()
    await expect(chip(canvas, 'Black-and-white pages, Squeeze volumes harder')).toHaveTextContent('WebPBalanced')
    await expect(chip(canvas, 'Colour pages, Squeeze volumes harder')).toHaveTextContent('Same as black-and-white')
    await expect(chip(canvas, 'Black-and-white pages, Everything else')).toHaveTextContent('WebPBalanced')
    await expect(canvas.getByText('Files no rule above matches')).toBeVisible()
  },
}

export const EstimateInFooter: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await expect(await canvas.findByText(/files, .* now, .* after$/)).toBeVisible()
    await expect(canvas.getByText(/Takes about/)).toBeVisible()
    await expect(canvas.getAllByText(/ after$/).length).toBeGreaterThan(0)

    await userEvent.click(canvas.getByRole('button', { name: 'Details' }))
    const dialog = within(await body(canvasElement).findByRole('dialog'))
    await expect(dialog.getByText('Passed over')).toBeVisible()
    await expect(dialog.getByText('Series with their own setting')).toBeVisible()
    await expect(dialog.getByText('Converted earlier')).toBeVisible()
  },
}

export const EstimateRunning: Story = {
  parameters: {
    msw: settingsMsw(
      http.get(api('/pageconversion/estimate'), () => HttpResponse.json(estimateRunning)),
      http.post(api('/pageconversion/estimate'), () => HttpResponse.json(estimateRunning)),
    ),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Sampling 14 of 36 files')).toBeVisible()
  },
}

export const EstimateFailed: Story = {
  parameters: {
    msw: settingsMsw(
      http.get(api('/pageconversion/estimate'), () => HttpResponse.json(estimateIdle)),
      http.post(api('/pageconversion/estimate'), () => HttpResponse.json(estimateFailed)),
    ),
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await expect(
      await canvas.findByText('The estimate did not finish. Open the details to try again.'),
    ).toBeVisible()
    await userEvent.click(canvas.getByRole('button', { name: 'Details' }))
    const dialog = within(await body(canvasElement).findByRole('dialog'))
    await expect(dialog.getByText('The estimate failed')).toBeVisible()
    await expect(dialog.getByRole('button', { name: 'Try again' })).toBeVisible()
  },
}

export const ColourTargetSheet: Story = {
  parameters: { msw: settingsMsw(settingsHandler(colourAvif)) },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Colour pages, Everything else' }))
    const dialog = within(await body(canvasElement).findByRole('dialog', { name: 'Colour pages, Everything else' }))
    await expect(await dialog.findByRole('button', { name: /Ashen Crown Regression.*Ch\.12/ })).toBeVisible()
    await expect(dialog.getByRole('radio', { name: 'AVIF' })).toBeChecked()
    await waitFor(() => expect(dialog.getByText(/, 62%$/)).toBeVisible())
    await waitFor(() => expect(dialog.getByText('As WebP')).toBeVisible())
    await waitFor(() => expect(dialog.getByText('As JPEG XL')).toBeVisible())

    dialog.getByRole('slider', { name: 'Quality' }).focus()
    await userEvent.keyboard('{Home}')
    await waitFor(() => expect(dialog.getByText(/, 27%$/)).toBeVisible())

    await userEvent.click(dialog.getByRole('button', { name: 'Done' }))
    await waitFor(() => expect(chip(canvas, 'Colour pages, Everything else')).toHaveTextContent('AVIFSmallest'))
  },
}

export const ColourSameAsBlackAndWhite: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Colour pages, Squeeze volumes harder' }))
    const dialog = within(await body(canvasElement).findByRole('dialog', { name: 'Colour pages, Squeeze volumes harder' }))
    await expect(dialog.getByRole('radio', { name: 'Same' })).toBeChecked()
    await expect(
      dialog.getByText('Colour pages are stored like black-and-white pages in this row: WebP, Balanced.'),
    ).toBeVisible()
    await expect(dialog.queryByRole('slider', { name: 'Quality' })).toBeNull()

    await userEvent.click(dialog.getByRole('radio', { name: 'AVIF' }))
    await expect(await dialog.findByRole('slider', { name: 'Quality' })).toBeVisible()
    await userEvent.click(dialog.getByRole('button', { name: 'Done' }))
    await waitFor(() => expect(chip(canvas, 'Colour pages, Squeeze volumes harder')).toHaveTextContent('AVIFBalanced'))
  },
}

export const BlackAndWhiteSheetUsesGreyPages: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Black-and-white pages, Everything else' }))
    const dialog = within(await body(canvasElement).findByRole('dialog'))
    await expect(await dialog.findByRole('button', { name: /Lanterns Over Kasumi/ })).toBeVisible()
    await expect(dialog.queryByRole('radio', { name: 'Same' })).toBeNull()
    await expect(await dialog.findByRole('img', { name: 'Converted page' })).toBeInTheDocument()

    await userEvent.click(dialog.getByRole('radio', { name: 'Fit' }))
    await expect(dialog.getByRole('radio', { name: 'Fit' })).toBeChecked()
  },
}

export const KeptPageSaysWhy: Story = {
  parameters: { msw: settingsMsw(keptPreviewHandler('notsmaller')) },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Black-and-white pages, Everything else' }))
    const dialog = within(await body(canvasElement).findByRole('dialog'))
    await expect(
      await dialog.findByText('Converting would not make this page at least 5% smaller, so the pass keeps it as it is.'),
    ).toBeVisible()
  },
}

export const NoPagesOfThatKind: Story = {
  parameters: {
    msw: settingsMsw(http.post(api('/pageconversion/samples/row'), () => HttpResponse.json({ samples: [] }))),
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Colour pages, Everything else' }))
    const dialog = within(await body(canvasElement).findByRole('dialog'))
    await expect(
      await dialog.findByText(
        'Maki found no colour pages in the files this row converts. Pick a page from your library to see the target on.',
      ),
    ).toBeVisible()
    await expect(dialog.getByRole('button', { name: 'Pick another page' })).toBeVisible()
  },
}

export const EditRuleConditions: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Edit rule 2' }))
    await expect(canvas.getByRole('textbox', { name: 'Name of rule 2' })).toHaveValue('Squeeze volumes harder')
    await userEvent.click(canvas.getByRole('button', { name: 'Add condition to rule 2' }))
    await userEvent.click(await body(canvasElement).findByRole('menuitem', { name: 'Series type' }))
    await expect(await canvas.findByRole('combobox', { name: 'Series type' })).toBeVisible()

    await userEvent.click(canvas.getByRole('radio', { name: 'Skipped' }))
    await waitFor(() => expect(canvas.queryByRole('button', { name: 'Black-and-white pages, Squeeze volumes harder' })).toBeNull())
    await expect(canvas.getAllByText('Skipped, left as they are')).toHaveLength(2)
  },
}

export const AddRuleFromTemplate: Story = {
  parameters: { msw: settingsMsw(noRules) },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await expect(await canvas.findByText('Every file')).toBeVisible()
    await userEvent.click(canvas.getByRole('button', { name: 'Start from a template' }))
    await userEvent.click(await body(canvasElement).findByRole('menuitem', { name: /Keep scanlator releases exact/ }))
    await expect(await canvas.findByText('1. Keep scanlator releases exact')).toBeVisible()
    await expect(chip(canvas, 'Black-and-white pages, Keep scanlator releases exact')).toHaveTextContent('Lossless')
    await expect(canvas.getByText('Files no rule above matches')).toBeVisible()
  },
}

export const SaveOnly: Story = {
  parameters: { msw: settingsMsw(recordSave) },
  play: async ({ canvas, canvasElement, userEvent }) => {
    saved = null
    await userEvent.click(await canvas.findByRole('button', { name: 'Colour pages, Everything else' }))
    const dialog = within(await body(canvasElement).findByRole('dialog'))
    await userEvent.click(dialog.getByRole('radio', { name: 'AVIF' }))
    await userEvent.click(dialog.getByRole('button', { name: 'Done' }))

    await userEvent.click(canvas.getByRole('button', { name: 'Save only' }))
    await expect(await body(canvasElement).findByText('Saved')).toBeVisible()
    expect(saved!.settings).toMatchObject({ format: 'webp', stop: 'balanced', colourFormat: 'avif', colourStop: 'balanced' })
    expect(saved!.rules.map((r) => r.name)).toEqual(pageConversionRules.rules.map((r) => r.name))
    expect(saved!.rules[0]).toMatchObject({ id: 1 })
  },
}

export const ConversionOff: Story = {
  parameters: { msw: settingsMsw(recordSave) },
  play: async ({ canvas, canvasElement, userEvent }) => {
    saved = null
    await userEvent.click(await canvas.findByRole('switch', { name: 'Convert pages' }))
    await expect(canvas.queryByRole('table', { name: 'Conversion plan' })).toBeNull()
    await expect(canvas.getByText('Conversion is off. Files converted earlier stay as they are.')).toBeVisible()
    await expect(canvas.queryByRole('button', { name: 'Save and start' })).toBeNull()

    await userEvent.click(canvas.getByRole('button', { name: 'Save only' }))
    await expect(await body(canvasElement).findByText('Saved')).toBeVisible()
    expect(saved!.settings.format).toBeNull()
  },
}

export const SaveWhilePassRunsAsksFirst: Story = {
  parameters: {
    msw: settingsMsw(http.get(api('/pageconversion'), () => HttpResponse.json(pageConversionRunning)), recordStart),
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    starts = 0
    await userEvent.click(await canvas.findByRole('button', { name: 'Black-and-white pages, Everything else' }))
    const sheet = within(await body(canvasElement).findByRole('dialog'))
    await userEvent.click(sheet.getByRole('radio', { name: 'WebP' }))
    await userEvent.click(sheet.getByRole('button', { name: 'Done' }))

    await userEvent.click(canvas.getByRole('button', { name: 'Save and start' }))
    const dialog = within(await body(canvasElement).findByRole('dialog', { name: 'Pause the running pass?' }))
    await userEvent.click(dialog.getByRole('button', { name: 'Restart' }))
    await waitFor(() => expect(starts).toBe(1))
  },
}

export const CountsFail: Story = {
  parameters: {
    msw: settingsMsw(http.post(api('/pageconversion/scope'), () => HttpResponse.json({ error: 'No' }, { status: 500 }))),
  },
  play: async ({ canvas }) => {
    await expect(
      await canvas.findByText('The counts could not be loaded. The rules still save as you wrote them.'),
    ).toBeVisible()
  },
}

export const RulesFailToLoad: Story = {
  parameters: {
    msw: settingsMsw(http.get(api('/pageconversion/rules'), () => HttpResponse.json({ error: 'No' }, { status: 500 }))),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Saved rules could not be loaded; saving will replace them.')).toBeVisible()
    await expect(canvas.getByText('Every file')).toBeVisible()
  },
}

export const SlowCounts: Story = {
  parameters: {
    msw: settingsMsw(
      http.post(api('/pageconversion/scope'), async () => {
        await delay(60_000)
        return HttpResponse.json(pageConversionScope)
      }),
    ),
  },
  play: async ({ canvas }) => {
    await expect((await canvas.findAllByText('Counting files')).length).toBeGreaterThan(0)
  },
}
