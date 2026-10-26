import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { delay, http, HttpResponse } from 'msw'
import { api } from '../../.storybook/mocks/api'
import { settingsMsw } from '../../.storybook/mocks/settings'
import { pageConversionIdle, pageConversionRunning } from '../../.storybook/mocks/settings/data'
import {
  estimateDone,
  estimateFailed,
  estimateIdle,
  estimateResult,
  estimateRunning,
  keptPreviewHandler,
  pageConversionRules,
  pageConversionScope,
  ruleLeaveScraped,
  startedEstimate,
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

const scopeRequests: Array<{ rules: Array<{ conditions: Record<string, unknown>; action: string; stop: string | null }> }> = []
const saves: string[] = []
let savedSettings: unknown = null
let savedRules: Array<{ id?: number | null; name: string; action: string; stop: string | null }> = []

const noRules = http.get(api('/pageconversion/rules'), () => HttpResponse.json({ rules: [] }))

const recordScope = http.post(api('/pageconversion/scope'), async ({ request }) => {
  const body = (await request.json()) as (typeof scopeRequests)[number]
  scopeRequests.push(body)
  return HttpResponse.json({
    ...pageConversionScope,
    rules: body.rules.map(() => ({ files: 12, bytes: 400 * 1024 ** 2 })),
  })
})

const ruleCard = (canvas: ReturnType<typeof within>, n: number) => {
  const li = canvas.getByRole('textbox', { name: `Name of rule ${n}` }).closest('li')
  if (!li) throw new Error(`Rule ${n} has no card`)
  return within(li)
}

export const Scope: Story = {
  parameters: atStep(3),
  play: async ({ canvas, userEvent }) => {
    await expect(await canvas.findByRole('textbox', { name: 'Name of rule 1' })).toHaveValue(ruleLeaveScraped.name)
    await expect(canvas.getByRole('textbox', { name: 'Name of rule 2' })).toHaveValue('Squeeze volumes harder')
    await waitFor(() => expect(ruleCard(canvas, 1).getByText(/1.?240 files, 3\.6 GB/)).toBeVisible())
    await waitFor(() => expect(ruleCard(canvas, 2).getByText(/365 files, 73\.8 GB/)).toBeVisible())
    await expect(ruleCard(canvas, 1).getByRole('combobox', { name: 'Action for rule 1' })).toHaveValue('Skip')
    await expect(ruleCard(canvas, 2).getByRole('combobox', { name: 'Action for rule 2' })).toHaveValue('Convert at Balanced')
    await expect(canvas.queryByText(/No conditions, so this rule matches/)).toBeNull()

    await expect(canvas.getByText('Everything else: Balanced')).toBeVisible()
    await expect(canvas.getByText(/94 files, 1\.9 GB/)).toBeVisible()
    await expect(canvas.getByText(/18 files are in series with their own setting/)).toBeVisible()
    await expect(canvas.getByText(/41 files are already WebP or AVIF/)).toBeVisible()
    await expect(canvas.getByText(/412 files were converted earlier/)).toBeVisible()

    await expect(canvas.getByRole('button', { name: 'Move rule 1 up' })).toBeDisabled()
    await expect(canvas.getByRole('button', { name: 'Move rule 2 down' })).toBeDisabled()
    await userEvent.click(canvas.getByRole('button', { name: 'Move rule 2 up' }))
    await expect(canvas.getByRole('textbox', { name: 'Name of rule 1' })).toHaveValue('Squeeze volumes harder')
    await expect(ruleCard(canvas, 1).getByText(/365 files/)).toBeVisible()
    await expect(ruleCard(canvas, 2).getByText(/1.?240 files/)).toBeVisible()

    await userEvent.click(canvas.getByRole('button', { name: 'Remove rule 1' }))
    await expect(canvas.queryByRole('textbox', { name: 'Name of rule 2' })).toBeNull()
    await expect(canvas.getByRole('textbox', { name: 'Name of rule 1' })).toHaveValue(ruleLeaveScraped.name)
  },
}

export const ScopeEmpty: Story = {
  parameters: { ...atStep(3), msw: settingsMsw(noRules) },
  play: async ({ canvas, userEvent }) => {
    await expect(await canvas.findByText('No rules yet. Every file is converted at Balanced.')).toBeVisible()
    await expect(canvas.getByText('Everything else: Balanced')).toBeVisible()
    for (const name of [
      'Leave scraped chapters alone',
      'Squeeze volumes harder',
      'Keep scanlator releases exact',
      'Never touch tagged series',
    ]) {
      await expect(canvas.getByRole('button', { name })).toBeEnabled()
    }

    await userEvent.click(canvas.getByRole('button', { name: 'Never touch tagged series' }))
    await expect(canvas.getByRole('textbox', { name: 'Name of rule 1' })).toHaveValue('Never touch tagged series')
    await expect(canvas.getByRole('combobox', { name: 'Action for rule 1' })).toHaveValue('Skip')
    await expect(canvas.getByText('keep-original')).toBeVisible()
    await expect(canvas.queryByText('No rules yet. Every file is converted at Balanced.')).toBeNull()
  },
}

export const ScopeAddRuleWarnsWhenItMatchesEverything: Story = {
  parameters: { ...atStep(3), msw: settingsMsw(noRules, recordScope) },
  play: async ({ canvas, canvasElement, userEvent }) => {
    scopeRequests.length = 0
    await userEvent.click(await canvas.findByRole('button', { name: 'Add rule' }))
    await expect(await canvas.findByText(/No conditions, so this rule matches every file that reaches it/)).toBeVisible()
    await expect(canvas.getByRole('combobox', { name: 'Action for rule 1' })).toHaveValue('Convert at Balanced')

    await userEvent.click(canvas.getByRole('button', { name: 'Add condition to rule 1' }))
    await userEvent.click(await body(canvasElement).findByRole('menuitem', { name: 'Bits per pixel' }))
    await expect(await canvas.findByRole('textbox', { name: 'Bits per pixel, under' })).toBeVisible()
    await expect(canvas.getByText(/No conditions, so this rule matches/)).toBeVisible()

    await userEvent.type(canvas.getByRole('textbox', { name: 'Bits per pixel, under' }), '1.5')
    await waitFor(() => expect(canvas.queryByText(/No conditions, so this rule matches/)).toBeNull())
    await waitFor(() => expect(scopeRequests.at(-1)?.rules[0].conditions).toEqual({ bppUnder: 1.5 }))
    // The editor that was opened but left empty was never sent.
    expect(scopeRequests.some((r) => r.rules.some((rule) => 'bppOver' in rule.conditions))).toBe(false)
    await waitFor(() => expect(canvas.getByText(/12 files, 400 MB/)).toBeVisible())

    await userEvent.click(canvas.getByRole('button', { name: 'Remove condition: Bits per pixel' }))
    await expect(canvas.queryByRole('textbox', { name: 'Bits per pixel, under' })).toBeNull()
    await expect(await canvas.findByText(/No conditions, so this rule matches/)).toBeVisible()
  },
}

export const ScopePickConditions: Story = {
  parameters: { ...atStep(3), msw: settingsMsw(noRules, recordScope) },
  play: async ({ canvas, canvasElement, userEvent }) => {
    scopeRequests.length = 0
    await userEvent.click(await canvas.findByRole('button', { name: 'Add rule' }))
    await userEvent.click(canvas.getByRole('button', { name: 'Add condition to rule 1' }))
    await userEvent.click(await body(canvasElement).findByRole('menuitem', { name: 'Page format' }))

    await userEvent.click(await canvas.findByRole('combobox', { name: 'Page format' }))
    await userEvent.click(await body(canvasElement).findByRole('option', { name: 'JPEG' }))

    await userEvent.click(canvas.getByRole('button', { name: 'Add condition to rule 1' }))
    await userEvent.click(await body(canvasElement).findByRole('menuitem', { name: 'File size' }))
    await userEvent.type(await canvas.findByRole('textbox', { name: 'File size in MB, over' }), '100')

    await waitFor(() =>
      expect(scopeRequests.at(-1)?.rules[0].conditions).toEqual({ formats: ['jpg'], sizeOverBytes: 104_857_600 }),
    )

    await userEvent.click(canvas.getByRole('combobox', { name: 'Action for rule 1' }))
    await userEvent.click(await body(canvasElement).findByRole('option', { name: 'Skip' }))
    await waitFor(() => expect(scopeRequests.at(-1)?.rules[0]).toMatchObject({ action: 'skip', stop: null }))
  },
}

export const ScopeWithNothingToReport: Story = {
  parameters: {
    ...atStep(3),
    msw: settingsMsw(
      http.post(api('/pageconversion/scope'), () =>
        HttpResponse.json({
          rules: [pageConversionScope.rules[0], { files: 0, bytes: 0 }],
          default: pageConversionScope.default,
          overrides: { files: 0, bytes: 0 },
          alreadyCompact: { files: 0, bytes: 0 },
          convertedEarlier: { files: 0, bytes: 0 },
        }),
      ),
    ),
  },
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getByText('No files match')).toBeVisible())
    await expect(canvas.getByText(/94 files, 1\.9 GB/)).toBeVisible()
    await expect(canvas.queryByText(/their own setting/)).toBeNull()
    await expect(canvas.queryByText(/already WebP or AVIF/)).toBeNull()
    await expect(canvas.queryByText(/converted earlier/)).toBeNull()
  },
}

export const ScopeCountsFail: Story = {
  parameters: {
    ...atStep(3),
    msw: settingsMsw(http.post(api('/pageconversion/scope'), () => HttpResponse.json({ error: 'No' }, { status: 500 }))),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/The counts could not be loaded/)).toBeVisible()
  },
}

export const ScopeWithConversionOff: Story = {
  parameters: atStep(1),
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('radio', { name: /Off/ }))
    await userEvent.click(canvas.getByRole('button', { name: 'Next' }))
    await userEvent.click(await canvas.findByRole('button', { name: 'Next' }))
    await expect(await canvas.findByText(/Conversion is off, so there is nothing to choose/)).toBeVisible()
    await expect(canvas.queryByRole('button', { name: 'Add rule' })).toBeNull()
  },
}

export const StartSavesOnePlan: Story = {
  parameters: {
    ...atStep(3),
    msw: settingsMsw(
      http.put(api('/pageconversion/plan'), async ({ request }) => {
        const plan = (await request.json()) as { settings: unknown; rules: typeof savedRules }
        saves.push('plan')
        savedRules = plan.rules
        savedSettings = plan.settings
        return HttpResponse.json(pageConversionIdle)
      }),
    ),
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    saves.length = 0
    savedRules = []
    await userEvent.click(await canvas.findByRole('button', { name: 'Keep scanlator releases exact' }))
    await userEvent.click(canvas.getByRole('button', { name: 'Next' }))
    await expect(await canvas.findByRole('button', { name: 'Re-estimate' })).toBeVisible()
    await userEvent.click(await canvas.findByRole('button', { name: 'Next' }))
    await expect(await canvas.findByText('3 rules, then everything else at the quality above')).toBeVisible()

    await userEvent.click(canvas.getByRole('button', { name: 'Save only' }))
    await expect(await body(canvasElement).findByText('Saved')).toBeVisible()
    expect(saves).toEqual(['plan'])
    expect(savedSettings).toEqual({ format: 'webp', stop: 'balanced', pngLossless: true })
    expect(savedRules.map((r) => r.name)).toEqual([
      pageConversionRules.rules[0].name,
      pageConversionRules.rules[1].name,
      'Keep scanlator releases exact',
    ])
    expect(savedRules[2]).toMatchObject({ action: 'convert', stop: 'lossless' })
    expect(savedRules[0]).toMatchObject({ id: 1 })
  },
}

export const StartSendsFullPlanWhenRulesUnchanged: Story = {
  parameters: {
    ...atStep(5),
    msw: settingsMsw(
      http.put(api('/pageconversion/plan'), async ({ request }) => {
        const plan = (await request.json()) as { rules: typeof savedRules }
        saves.push(`plan:${plan.rules.length}`)
        return HttpResponse.json(pageConversionIdle)
      }),
    ),
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    saves.length = 0
    await userEvent.click(await canvas.findByRole('button', { name: 'Save only' }))
    await expect(await body(canvasElement).findByText('Saved')).toBeVisible()
    expect(saves).toEqual(['plan:2'])
  },
}

export const ScopeWhenRulesFailToLoad: Story = {
  parameters: {
    ...atStep(3),
    msw: settingsMsw(http.get(api('/pageconversion/rules'), () => HttpResponse.json({ error: 'No' }, { status: 500 }))),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Saved rules could not be loaded; saving will replace them.')).toBeVisible()
    await expect(canvas.getByText(/No rules yet/)).toBeVisible()
    await expect(canvas.getByRole('button', { name: 'Add rule' })).toBeEnabled()
  },
}

let posts = 0

const estimateMsw = (
  onPost: (n: number) => Response | Promise<Response>,
  onGet: (posted: number) => object = () => estimateIdle,
) =>
  settingsMsw(
    http.get(api('/pageconversion/estimate'), () => HttpResponse.json(onGet(posts))),
    http.post(api('/pageconversion/estimate'), () => {
      posts += 1
      return onPost(posts)
    }),
  )

const json = (value: object) => HttpResponse.json(value)

const resetPosts = () => {
  posts = 0
}

export const Review: Story = {
  parameters: atStep(4),
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('columnheader', { name: 'Expected' })).toBeVisible()
    await expect(canvas.getByText('Leave scraped chapters alone')).toBeVisible()
    await expect(canvas.getByText('Skipped: 1,527 files, 18.1 GB')).toBeVisible()
    await expect(canvas.getByText('Squeeze volumes harder')).toBeVisible()
    await expect(canvas.getByText('48 GB to 53 GB')).toBeVisible()
    await expect(canvas.getByText('35m to 1h 10m')).toBeVisible()
    await expect(canvas.getByText('About 32.9 GB are hardlinked to torrents and stay as they are.')).toBeVisible()
    await expect(canvas.getByText('Everything else: Balanced')).toBeVisible()
    await expect(canvas.getByText('Series with their own stop')).toBeVisible()

    await expect(canvas.getByText('Passed over')).toBeVisible()
    await expect(canvas.getByText('143 files are hardlinked to torrents and stay as they are (32.9 GB)')).toBeVisible()
    await expect(canvas.getByText('22 files are due for read-file cleanup')).toBeVisible()
    await expect(canvas.getByText('41 files are already WebP or AVIF')).toBeVisible()
    await expect(canvas.getByText('3 files are in series with a download in flight')).toBeVisible()
    await expect(canvas.getByText('9 files are waiting on an import link')).toBeVisible()
    await expect(canvas.getByText('1,527 files are skipped by a rule (18.1 GB)')).toBeVisible()
    await expect(canvas.getByText('56 files are in 2 series set to Never (4.2 GB)')).toBeVisible()
    await expect(canvas.getByText('7 files were put back by an undo and stay as they are')).toBeVisible()
    await expect(canvas.getByText('212 files were already checked with these settings (5.6 GB)')).toBeVisible()

    await expect(canvas.getByText('Converted earlier')).toBeVisible()
    await expect(canvas.getByText('WebP, High: 1,204 files, 9.8 GB saved')).toBeVisible()
    await expect(canvas.getByText('AVIF, Balanced, PNG lossless: 80 files, 2.4 GB saved')).toBeVisible()
    await expect(canvas.getByText('Settings not recorded: 12 files, 307 MB saved')).toBeVisible()
    await expect(canvas.getByText(/37 of them still have their original in the trash/)).toBeVisible()
  },
}

export const ReviewRunning: Story = {
  parameters: { ...atStep(4), msw: estimateMsw(() => json(estimateRunning), () => estimateRunning) },
  beforeEach: resetPosts,
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Sampling 14 of 36 files')).toBeVisible()
    await expect(canvas.getByRole('progressbar', { name: 'Estimate progress' })).toBeVisible()
    await expect(canvas.queryByRole('columnheader', { name: 'Expected' })).toBeNull()
    await expect(canvas.getByRole('button', { name: 'Next' })).toBeEnabled()
  },
}

export const ReviewRunningThenDone: Story = {
  parameters: {
    ...atStep(4),
    msw: estimateMsw(
      () => json(startedEstimate),
      (n) => (n === 0 ? estimateIdle : estimateDone),
    ),
  },
  beforeEach: resetPosts,
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Sampling 0 of 36 files')).toBeVisible()
    await expect(await canvas.findByRole('columnheader', { name: 'Expected' })).toBeVisible()
    await expect(canvas.queryByText(/Sampling/)).toBeNull()
  },
}

export const ReviewReestimateKeepsResult: Story = {
  parameters: {
    ...atStep(4),
    msw: estimateMsw(
      (n) => json(n === 1 ? estimateDone : estimateRunning),
      (n) => (n < 2 ? estimateIdle : estimateRunning),
    ),
  },
  beforeEach: resetPosts,
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Re-estimate' }))
    await expect(await canvas.findByText('Sampling 14 of 36 files')).toBeVisible()
    await expect(canvas.getByRole('columnheader', { name: 'Expected' })).toBeVisible()
    await expect(canvas.getByText('48 GB to 53 GB')).toBeVisible()
    await expect(canvas.getByRole('button', { name: 'Re-estimate' })).toBeDisabled()
  },
}

export const ReviewFailedThenRetry: Story = {
  parameters: { ...atStep(4), msw: estimateMsw((n) => json(n === 1 ? estimateFailed : estimateDone)) },
  beforeEach: resetPosts,
  play: async ({ canvas, userEvent }) => {
    await expect(await canvas.findByText('The estimate failed')).toBeVisible()
    await expect(canvas.getByText('The estimate failed on this server. Try again, or check the log.')).toBeVisible()
    await expect(canvas.queryByText('The library could not be read while sampling.')).toBeNull()
    await userEvent.click(canvas.getByRole('button', { name: 'Try again' }))
    await expect(await canvas.findByRole('columnheader', { name: 'Expected' })).toBeVisible()
    await expect(canvas.queryByText('The estimate failed')).toBeNull()
    await waitFor(() => expect(posts).toBe(2))
  },
}

export const ReviewCancelled: Story = {
  parameters: { ...atStep(4), msw: estimateMsw(() => json({ ...estimateFailed, state: 'cancelled', error: null })) },
  beforeEach: resetPosts,
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('The estimate was cancelled')).toBeVisible()
    await expect(canvas.getByRole('button', { name: 'Try again' })).toBeVisible()
  },
}

export const ReviewCouldNotStart: Story = {
  parameters: {
    ...atStep(4),
    msw: estimateMsw(() => HttpResponse.json({ error: 'The plan is not valid.' }, { status: 400 })),
  },
  beforeEach: resetPosts,
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('The estimate could not be started')).toBeVisible()
    await expect(canvas.getByRole('button', { name: 'Try again' })).toBeVisible()
  },
}

export const ReviewKeepsResultWhenRevisited: Story = {
  parameters: {
    ...atStep(4),
    msw: estimateMsw(
      () => json(estimateDone),
      (n) => (n === 0 ? estimateIdle : estimateDone),
    ),
  },
  beforeEach: resetPosts,
  play: async ({ canvas, userEvent }) => {
    await expect(await canvas.findByRole('columnheader', { name: 'Expected' })).toBeVisible()
    await userEvent.click(canvas.getByRole('button', { name: 'Back' }))
    await userEvent.click(await canvas.findByRole('button', { name: 'Next' }))
    await expect(await canvas.findByRole('columnheader', { name: 'Expected' })).toBeVisible()
    expect(posts).toBe(1)
  },
}

let gets = 0

export const ReviewRevisitedWithSlowRefetchShowsNewEstimate: Story = {
  parameters: {
    ...atStep(3),
    msw: settingsMsw(
      http.get(api('/pageconversion/estimate'), async () => {
        gets += 1
        if (gets === 1) return json(estimateIdle)
        await delay(600)
        return json(estimateDone)
      }),
      http.post(api('/pageconversion/estimate'), () => {
        posts += 1
        return json(posts === 1 ? estimateDone : { ...estimateRunning, done: 5, total: 20, planHash: 'plan-b' })
      }),
    ),
  },
  beforeEach: () => {
    posts = 0
    gets = 0
  },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Next' }))
    await expect(await canvas.findByRole('columnheader', { name: 'Expected' })).toBeVisible()
    await userEvent.click(canvas.getByRole('button', { name: 'Back' }))
    await userEvent.click(await canvas.findByRole('button', { name: 'Keep scanlator releases exact' }))
    await userEvent.click(canvas.getByRole('button', { name: 'Next' }))

    await expect(await canvas.findByText('Sampling 5 of 20 files')).toBeVisible()
    await waitFor(() => expect(gets).toBeGreaterThanOrEqual(2))
    await new Promise((resolve) => setTimeout(resolve, 900))
    await expect(canvas.getByText('Sampling 5 of 20 files')).toBeVisible()
    expect(posts).toBe(2)
  },
}

export const ReviewNothingSampledNothingPassedOver: Story = {
  parameters: {
    ...atStep(4),
    msw: estimateMsw(() =>
      json({
        ...estimateDone,
        result: {
          ...estimateResult,
          rules: [
            { ...estimateResult.rules[0], skip: false, files: 0, bytes: 0 },
            { ...estimateResult.rules[1], sampledFiles: 0, hardlinkedBytes: 0 },
          ],
          overrides: { ...estimateResult.overrides, files: 0, bytes: 0 },
          passedOver: {
            hardlinkedFiles: 0,
            hardlinkedBytes: 0,
            cleanupDue: 0,
            alreadyCompact: 0,
            downloadInFlight: 0,
            pendingImport: 0,
            skippedByRule: 0,
            skippedByRuleBytes: 0,
            neverSeries: 0,
            neverFiles: 0,
            neverBytes: 0,
            undone: 0,
            alreadyChecked: 0,
            alreadyCheckedBytes: 0,
          },
          convertedEarlier: [],
          convertedWithOriginalInTrash: 0,
        },
      }),
    ),
  },
  beforeEach: resetPosts,
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Not sampled')).toBeVisible()
    await expect(canvas.getByText('No files')).toBeVisible()
    await expect(canvas.queryByText('Passed over')).toBeNull()
    await expect(canvas.queryByText('Converted earlier')).toBeNull()
    await expect(canvas.queryByText('Series with their own stop')).toBeNull()
  },
}

export const Start: Story = {
  parameters: atStep(5),
  play: async ({ canvas, canvasElement, userEvent }) => {
    await expect(await canvas.findByText(/Balanced\. Tone is a little lighter/)).toBeVisible()
    await expect(canvas.getByText('Kept lossless')).toBeVisible()
    await expect(await canvas.findByText('2 rules, then everything else at the quality above')).toBeVisible()
    await expect(canvas.getByRole('button', { name: 'Save and start' })).toBeEnabled()

    await userEvent.click(canvas.getByRole('button', { name: 'Save only' }))
    await expect(await body(canvasElement).findByText('Saved')).toBeVisible()
  },
}

export const StartWithoutRules: Story = {
  parameters: {
    ...atStep(5),
    msw: settingsMsw(http.get(api('/pageconversion/rules'), () => HttpResponse.json({ rules: [] }))),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('None, every file uses the quality above')).toBeVisible()
  },
}

export const StartWithConversionOff: Story = {
  parameters: atStep(1),
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('radio', { name: /Off/ }))
    await userEvent.click(canvas.getByRole('button', { name: 'Next' }))
    await userEvent.click(await canvas.findByRole('button', { name: 'Next' }))
    await expect(await canvas.findByText(/Conversion is off, so there is nothing to choose/)).toBeVisible()
    await userEvent.click(canvas.getByRole('button', { name: 'Next' }))
    await expect(await canvas.findByText(/Conversion is off, so there is nothing to estimate/)).toBeVisible()
    await userEvent.click(canvas.getByRole('button', { name: 'Next' }))
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
    await userEvent.click(await canvas.findByRole('button', { name: 'Next' }))
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
    await userEvent.click(await canvas.findByRole('button', { name: 'Next' }))
    await userEvent.click(await canvas.findByRole('button', { name: 'Next' }))
    await userEvent.click(await canvas.findByRole('button', { name: 'Save only' }))

    const dialog = await body(canvasElement).findByRole('dialog')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Restart' }))
    await waitFor(() => expect(starts).toBe(1))
  },
}
