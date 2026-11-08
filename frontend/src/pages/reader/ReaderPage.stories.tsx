import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith, neverResolve } from '../../../.storybook/mocks/api'
import { chapters } from '../../../.storybook/mocks/entry/fixtures'
import type { ReaderManifest, ServerPageState } from '../../api/reader'
import ReaderPage from './ReaderPage'
import type { ReaderPrefs } from './prefs'

const meta = {
  component: ReaderPage,
  tags: ['ai-generated'],
  parameters: {
    layout: 'fullscreen',
    router: { path: '/read/:chapterId', initialEntries: ['/read/1132'] },
  },
} satisfies Meta<typeof ReaderPage>

export default meta
type Story = StoryObj<typeof meta>

const open = (chapterId: number) => ({ router: { path: '/read/:chapterId', initialEntries: [`/read/${chapterId}`] } })

const withManifest = (manifest: ReaderManifest) => http.get(api('/reader/chapter/:id'), () => HttpResponse.json(manifest))

type PlayArgs = Parameters<NonNullable<Story['play']>>[0]

/** A tap in the middle of the page is what summons the toolbars. */
async function summonChrome({ canvasElement, userEvent }: Pick<PlayArgs, 'canvasElement' | 'userEvent'>) {
  const surface = canvasElement.querySelector<HTMLElement>('.reader-surface')
  if (!surface) throw new Error('The reader surface did not render')
  const box = surface.getBoundingClientRect()
  await userEvent.pointer({
    keys: '[MouseLeft]',
    target: surface,
    coords: { clientX: box.left + box.width / 2, clientY: box.top + box.height / 2 },
  })
}

/** Resumes on page 6 of 18 and shows it: the page has to be in before anything else is true. */
export const PagedManga: Story = {
  parameters: open(1132),
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('img', { name: 'Chapter 132 - page 6' })).toBeVisible()
    await expect(await canvas.findByText('Page 6 of 18')).toBeInTheDocument()
  },
}

export const ToolbarOpen: Story = {
  parameters: open(1132),
  play: async ({ canvas, canvasElement, userEvent }) => {
    await canvas.findByRole('img', { name: 'Chapter 132 - page 6' })
    await summonChrome({ canvasElement, userEvent })
    await expect(await canvas.findByRole('link', { name: 'Back to series' })).toBeVisible()
    await expect(await canvas.findByRole('button', { name: 'Toggle bookmark' })).toBeVisible()
    await expect(await canvas.findByText('6 / 18')).toBeVisible()
  },
}

export const SettingsOpen: Story = {
  parameters: open(1132),
  play: async ({ canvas, canvasElement, userEvent }) => {
    await canvas.findByRole('img', { name: 'Chapter 132 - page 6' })
    await summonChrome({ canvasElement, userEvent })
    await userEvent.click(await canvas.findByRole('button', { name: 'Reader settings' }))
    const body = within(canvasElement.ownerDocument.body)
    await expect(await body.findByRole('button', { name: /^Reading profile: / })).toBeVisible()
    await waitFor(() =>
      expect(canvasElement.ownerDocument.querySelector('.reader-settings-scope')).toHaveTextContent(/profile and every series using it/),
    )
  },
}

export const ChangingLayoutInSettings: Story = {
  parameters: open(1132),
  play: async ({ canvas, canvasElement, userEvent }) => {
    await canvas.findByRole('img', { name: 'Chapter 132 - page 6' })
    await summonChrome({ canvasElement, userEvent })
    await userEvent.click(await canvas.findByRole('button', { name: 'Reader settings' }))
    const body = within(canvasElement.ownerDocument.body)
    await userEvent.click(await body.findByText('Continuous'))
    // Continuous mode lays every page out, so a page far from the resume point is in the document.
    await expect(await canvas.findByRole('img', { name: 'Chapter 132 - page 18' }, { timeout: 5000 })).toBeInTheDocument()
  },
}

export const ThumbnailStrip: Story = {
  parameters: open(1132),
  play: async ({ canvas, userEvent }) => {
    await canvas.findByRole('img', { name: 'Chapter 132 - page 6' })
    await userEvent.keyboard('t')
    // Bookmarks come from the server; page 4 is one of the two in the fixture.
    await expect(await canvas.findByRole('button', { name: 'Go to page 4 (bookmarked)' })).toBeInTheDocument()
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Go to page 6' })).toHaveAttribute('aria-current', 'true'))
  },
}

export const NoteMarkersOnThumbnails: Story = {
  parameters: open(1132),
  play: async ({ canvas, userEvent }) => {
    await canvas.findByRole('img', { name: 'Chapter 132 - page 6' })
    await userEvent.keyboard('t')
    await expect(await canvas.findByRole('button', { name: 'Go to page 2 (has notes)' })).toHaveAttribute('data-noted', 'true')
    await expect(await canvas.findByRole('button', { name: 'Go to page 12 (has notes)' })).toBeInTheDocument()
  },
}

export const NotesPanelOpens: Story = {
  parameters: open(1132),
  play: async ({ canvas, canvasElement, userEvent }) => {
    await canvas.findByRole('img', { name: 'Chapter 132 - page 6' })
    await summonChrome({ canvasElement, userEvent })
    await userEvent.click(await canvas.findByRole('button', { name: 'Notes (3)' }))
    const body = within(canvasElement.ownerDocument.body)
    await expect(await body.findByRole('dialog', { name: 'Notes, Chapter 132' })).toBeVisible()
    await expect(await body.findByText(/tea cups/)).toBeVisible()
    // Escape closes the panel and leaves the reader where it is.
    await userEvent.keyboard('{Escape}')
    await waitFor(() => expect(body.queryByRole('dialog', { name: 'Notes, Chapter 132' })).toBeNull())
    await expect(await canvas.findByRole('img', { name: 'Chapter 132 - page 6' })).toBeVisible()
  },
}

export const NotesPanelKeepsPageKeys: Story = {
  parameters: open(1132),
  play: async ({ canvas, canvasElement, userEvent }) => {
    await canvas.findByRole('img', { name: 'Chapter 132 - page 6' })
    await summonChrome({ canvasElement, userEvent })
    await userEvent.click(await canvas.findByRole('button', { name: 'Notes (3)' }))
    const body = within(canvasElement.ownerDocument.body)
    await userEvent.type(await body.findByRole('textbox', { name: 'Write a note' }), 'Typing {ArrowLeft}b t')
    // Keys typed into the box never reach the page behind it.
    await expect(await canvas.findByRole('img', { name: 'Chapter 132 - page 6' })).toBeVisible()
  },
}

export const NotesFromAnInboxLink: Story = {
  parameters: { router: { path: '/read/:chapterId', initialEntries: ['/read/1132?notes=3'] } },
  play: async ({ canvasElement }) => {
    const body = within(canvasElement.ownerDocument.body)
    await expect(await body.findByRole('dialog', { name: 'Notes, Chapter 132' })).toBeVisible()
    await waitFor(() => expect(canvasElement.ownerDocument.getElementById('note-thread-3')).toHaveAttribute('data-highlight'))
  },
}
export const KeyboardShortcuts: Story = {
  parameters: open(1132),
  play: async ({ canvas, userEvent }) => {
    await canvas.findByRole('img', { name: 'Chapter 132 - page 6' })
    await userEvent.keyboard('?')
    const dialog = await canvas.findByRole('dialog', { name: 'Keyboard shortcuts' })
    await waitFor(() => expect(dialog).toBeVisible())
    await userEvent.keyboard('{Escape}')
    await expect(canvas.queryByRole('dialog')).not.toBeInTheDocument()
  },
}

export const PagingForward: Story = {
  parameters: open(1132),
  play: async ({ canvas, userEvent }) => {
    await canvas.findByRole('img', { name: 'Chapter 132 - page 6' })
    // The default direction is right to left, so the left arrow is "next".
    await userEvent.keyboard('{ArrowLeft}')
    await expect(await canvas.findByRole('img', { name: 'Chapter 132 - page 7' })).toBeVisible()
  },
}

export const ContinuousWebtoon: Story = {
  parameters: open(3040),
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('img', { name: 'Chapter 40 - page 1' })).toBeVisible()
  },
}

/** The bottom of the screen moves the strip down most of a screen, Shift + Space brings it back. */
export const ContinuousTapMovesAScreen: Story = {
  parameters: open(3040),
  play: async ({ canvas, canvasElement, userEvent }) => {
    await canvas.findByRole('img', { name: 'Chapter 40 - page 1' })
    const surface = canvasElement.querySelector<HTMLElement>('.reader-surface')!
    await waitFor(() => expect(surface.scrollHeight).toBeGreaterThan(surface.clientHeight * 2))
    const start = surface.scrollTop
    const box = surface.getBoundingClientRect()
    await userEvent.pointer({
      keys: '[MouseLeft]',
      target: surface,
      coords: { clientX: box.left + box.width / 2, clientY: box.top + box.height * 0.9 },
    })
    await waitFor(() => expect(surface.scrollTop).toBeGreaterThan(start + surface.clientHeight * 0.8))
    await expect(canvasElement.querySelector('.reader-bar-bottom')).toHaveAttribute('data-visible', 'false')
    await userEvent.keyboard('{Shift>} {/Shift}')
    await waitFor(() => expect(Math.abs(surface.scrollTop - start)).toBeLessThan(4))
  },
}

export const ContinuousAutoScroll: Story = {
  parameters: open(3040),
  play: async ({ canvas, canvasElement, userEvent }) => {
    await canvas.findByRole('img', { name: 'Chapter 40 - page 1' })
    const surface = canvasElement.querySelector<HTMLElement>('.reader-surface')!
    await waitFor(() => expect(surface.scrollHeight).toBeGreaterThan(surface.clientHeight * 2))
    await summonChrome({ canvasElement, userEvent })
    await userEvent.click(await canvas.findByRole('button', { name: 'Auto-scroll' }))
    const start = surface.scrollTop
    await waitFor(() => expect(surface.scrollTop).toBeGreaterThan(start + 20))
  },
}

// Chapter ids of their own: with isolate off, page URLs of a chapter other stories open are already
// in the image cache as portrait pages, and these handlers would never be asked.
const stripPages = (chapterId: number) =>
  http.get(api(`/reader/chapter/${chapterId}/page/:n`), ({ params }) =>
    HttpResponse.redirect(`/mock/strip/${chapterId}/${Number(params.n) + 1}.svg`),
  )
const spreadPages = (chapterId: number) =>
  http.get(api(`/reader/chapter/${chapterId}/page/:n`), ({ params }) =>
    HttpResponse.redirect(`/mock/spread/${chapterId}/${Number(params.n) + 1}.svg`),
  )

/** Paged by default, but every page is a tall strip slice, so the chapter opens scrolling. */
export const WebtoonChapterOpensContinuous: Story = {
  parameters: {
    ...open(7701),
    msw: [
      withManifest({
        ...chapters[1132],
        chapterId: 7701,
        prefsSource: 'Global',
        prefs: { ...chapters[1132].prefs, autoWebtoon: true },
      }),
      stripPages(7701),
    ],
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await canvas.findByRole('img', { name: 'Chapter 132 - page 6' })
    await expect(canvasElement.querySelector('.reader-continuous')).not.toBeNull()
    await summonChrome({ canvasElement, userEvent })
    await userEvent.click(await canvas.findByRole('button', { name: 'Reader settings' }))
    const popover = within(canvasElement.ownerDocument.body)
    await expect(await popover.findByText("Continuous, because this chapter's pages are very tall.")).toBeVisible()
  },
}

/** A profile's layout is a choice, so tall pages leave it alone. */
export const ProfileLayoutBeatsWebtoonDetection: Story = {
  parameters: { ...open(7702), msw: [withManifest({ ...chapters[1132], chapterId: 7702 }), stripPages(7702)] },
  play: async ({ canvas, canvasElement }) => {
    await canvas.findByRole('img', { name: 'Chapter 132 - page 6' })
    await expect(canvasElement.querySelector('.reader-continuous')).toBeNull()
  },
}

/** Each half of a spread is a turn of its own, but both are page 6 as far as progress goes. */
export const SplitSpreadRightToLeft: Story = {
  parameters: {
    ...open(7703),
    msw: [
      withManifest({ ...chapters[1132], chapterId: 7703, prefs: { ...chapters[1132].prefs, splitWide: 'split' } }),
      spreadPages(7703),
    ],
  },
  play: async ({ canvas, userEvent }) => {
    const first = await canvas.findByRole('img', { name: 'Chapter 132 - page 6' })
    await expect(first.style.left).toBe('-100%')
    await userEvent.keyboard('{ArrowLeft}')
    await expect((await canvas.findByRole('img', { name: 'Chapter 132 - page 6' })).style.left).toBe('0%')
    await expect(await canvas.findByText('Page 6 of 18')).toBeInTheDocument()
    await userEvent.keyboard('{ArrowLeft}')
    await expect((await canvas.findByRole('img', { name: 'Chapter 132 - page 7' })).style.left).toBe('-100%')
    // Back again lands on the half read last.
    await userEvent.keyboard('{ArrowRight}')
    await expect((await canvas.findByRole('img', { name: 'Chapter 132 - page 6' })).style.left).toBe('0%')
  },
}

const seriesPrefsSaves: ReaderManifest['prefs'][] = []

/** Picking a layout over a detected webtoon is kept for the series with detection off, so it sticks. */
export const HandPickedLayoutStaysWithTheSeries: Story = {
  parameters: {
    ...open(7704),
    msw: [
      withManifest({
        ...chapters[1132],
        chapterId: 7704,
        prefsSource: 'Global',
        prefs: { ...chapters[1132].prefs, autoWebtoon: true },
      }),
      stripPages(7704),
      http.put(api('/reader/series/:id/prefs'), async ({ request }) => {
        const { prefs } = (await request.json()) as { prefs: ReaderManifest['prefs'] }
        seriesPrefsSaves.push(prefs)
        return HttpResponse.json({ prefs, source: 'Series', profileId: null, pinnedProfileId: null, autoProfileId: 1 })
      }),
    ],
  },
  beforeEach: () => {
    seriesPrefsSaves.length = 0
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await canvas.findByRole('img', { name: 'Chapter 132 - page 6' })
    await waitFor(() => expect(canvasElement.querySelector('.reader-continuous')).not.toBeNull())
    await userEvent.keyboard('1')
    await waitFor(() => expect(seriesPrefsSaves.at(-1)).toMatchObject({ mode: 'paged', autoWebtoon: false }))
    await waitFor(() => expect(canvasElement.querySelector('.reader-continuous')).toBeNull())
  },
}

const progressWrites: { pageIndex: number; completed?: boolean }[] = []

/** The first half of a wide last page is not the end: the chapter completes on the second half. */
export const SplitLastPageCompletesOnItsSecondHalf: Story = {
  parameters: {
    ...open(7705),
    msw: [
      withManifest({
        ...chapters[1132],
        chapterId: 7705,
        resumePage: 15,
        prefs: { ...chapters[1132].prefs, splitWide: 'split' },
      }),
      spreadPages(7705),
      http.put(api('/reader/chapter/:id/progress'), async ({ request }) => {
        progressWrites.push((await request.json()) as { pageIndex: number; completed?: boolean })
        return new HttpResponse(null, { status: 204 })
      }),
    ],
  },
  beforeEach: () => {
    progressWrites.length = 0
  },
  play: async ({ canvas, userEvent }) => {
    await canvas.findByRole('img', { name: 'Chapter 132 - page 16' })
    for (let turn = 0; turn < 4; turn++) await userEvent.keyboard('{ArrowLeft}')
    await expect((await canvas.findByRole('img', { name: 'Chapter 132 - page 18' })).style.left).toBe('-100%')
    await waitFor(() => expect(progressWrites.some((write) => write.pageIndex === 16)).toBe(true))
    await expect(progressWrites.every((write) => write.pageIndex < 17 && !write.completed)).toBe(true)
    await userEvent.keyboard('{ArrowLeft}')
    await expect((await canvas.findByRole('img', { name: 'Chapter 132 - page 18' })).style.left).toBe('0%')
    await waitFor(() => expect(progressWrites.some((write) => write.pageIndex === 17)).toBe(true))
  },
}

/** A page reached while scrolling starts on its first half once the layout is paged again. */
export const PageFromTheStripStartsOnTheFirstHalf: Story = {
  parameters: {
    ...open(7706),
    msw: [
      withManifest({ ...chapters[1132], chapterId: 7706, prefs: { ...chapters[1132].prefs, splitWide: 'split' } }),
      spreadPages(7706),
    ],
  },
  play: async ({ canvas, userEvent }) => {
    await canvas.findByRole('img', { name: 'Chapter 132 - page 6' })
    await userEvent.keyboard('{ArrowLeft}')
    await expect((await canvas.findByRole('img', { name: 'Chapter 132 - page 6' })).style.left).toBe('0%')
    await userEvent.keyboard('3')
    await userEvent.keyboard('1')
    await expect((await canvas.findByRole('img', { name: 'Chapter 132 - page 6' })).style.left).toBe('-100%')
  },
}

export const DoublePage: Story = {
  parameters: open(1201),
  play: async ({ canvas, userEvent }) => {
    await canvas.findByRole('img', { name: 'Chapter 1: The first letter - page 1' })
    // The cover stands alone; the next turn shows pages 2 and 3 side by side.
    await userEvent.keyboard('{ArrowLeft}')
    await expect(await canvas.findByRole('img', { name: 'Chapter 1: The first letter - page 2' })).toBeVisible()
    await expect(await canvas.findByRole('img', { name: 'Chapter 1: The first letter - page 3' })).toBeVisible()
  },
}

/** Auto-advance is off for this series, so running off the last page lands on the end screen. */
export const EndOfChapter: Story = {
  parameters: open(6019),
  play: async ({ canvas, userEvent }) => {
    await canvas.findByRole('img', { name: 'Chapter 19 - page 14' })
    await userEvent.keyboard('{ArrowLeft}')
    await expect(await canvas.findByRole('heading', { name: 'Chapter 19' })).toBeVisible()
    await expect(await canvas.findByText("Chapters 20 to 21 aren't downloaded, so this skips them.", { exact: false })).toBeInTheDocument()
    await expect(await canvas.findByRole('button', { name: /Read Chapter 22/ })).toBeVisible()
  },
}

/** A quick second press on the last page stops at the end screen instead of carrying on into Chapter 22. */
export const DoublePressStopsAtTheEnd: Story = {
  parameters: open(6019),
  play: async ({ canvas, userEvent }) => {
    await canvas.findByRole('img', { name: 'Chapter 19 - page 14' })
    await userEvent.keyboard('{ArrowLeft}{ArrowLeft}')
    await new Promise((resolve) => setTimeout(resolve, 500))
    await expect(await canvas.findByRole('heading', { name: 'Chapter 19' })).toBeVisible()
    await expect(await canvas.findByRole('button', { name: /Read Chapter 22/ })).toBeVisible()
  },
}

export const StayHereAfterTheEnd: Story = {
  parameters: open(6019),
  play: async ({ canvas, userEvent }) => {
    await canvas.findByRole('img', { name: 'Chapter 19 - page 14' })
    await userEvent.keyboard('{ArrowLeft}')
    await userEvent.click(await canvas.findByRole('button', { name: 'Stay here' }))
    await expect(await canvas.findByRole('img', { name: 'Chapter 19 - page 14' })).toBeVisible()
  },
}

export const EndOfFinishedSeries: Story = {
  parameters: open(2096),
  play: async ({ canvas, canvasElement, userEvent }) => {
    await canvas.findByRole('img', { name: 'Chapter 96: The ninth bowl - page 24' })
    await userEvent.keyboard('{ArrowLeft}')
    await expect(await canvas.findByText("You're caught up")).toBeVisible()
    const end = within(canvasElement.querySelector<HTMLElement>('.reader-end')!)
    await expect(await end.findByRole('link', { name: 'Back to series' })).toBeVisible()
  },
}

export const EndWithMoreChaptersWanted: Story = {
  parameters: open(1142),
  play: async ({ canvas, userEvent }) => {
    await canvas.findByRole('img', { name: 'Chapter 142 - page 12' })
    await userEvent.keyboard('{ArrowLeft}')
    await expect(await canvas.findByText('8 more chapters are wanted. They show up here once they download.')).toBeVisible()
  },
}

export const OneShot: Story = {
  parameters: open(9001),
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('img', { name: 'Special: Midnight edition - page 1' })).toBeVisible()
  },
}

export const NightReading: Story = {
  parameters: {
    ...open(1132),
    msw: [
      withManifest({
        ...chapters[1132],
        prefs: { ...chapters[1132].prefs, dim: 35, filter: 'sepia', background: '#000000', showPageNumber: true },
      }),
    ],
  },
}

export const LeftToRightWithPageBadge: Story = {
  parameters: {
    ...open(1132),
    msw: [
      withManifest({
        ...chapters[1132],
        prefs: { ...chapters[1132].prefs, direction: 'ltr', fit: 'width' },
        previousChapterId: null,
        resumePage: 0,
      }),
    ],
  },
  play: async ({ canvas, canvasElement }) => {
    await canvas.findByRole('img', { name: 'Chapter 132 - page 1' })
    await waitFor(() => expect(canvasElement.querySelector('.reader-page-badge')).toHaveTextContent('1 / 18'))
  },
}

export const Mobile: Story = {
  parameters: { ...open(1132), viewport: { defaultViewport: 'mobile1' } },
  globals: { viewport: { value: 'mobile1', isRotated: false } },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('img', { name: 'Chapter 132 - page 6' })).toBeVisible()
  },
}

export const MobileWebtoon: Story = {
  parameters: { ...open(3040), viewport: { defaultViewport: 'mobile1' } },
  globals: { viewport: { value: 'mobile1', isRotated: false } },
}

export const Loading: Story = {
  parameters: { ...open(1132), msw: [http.get(api('/reader/chapter/:id'), () => neverResolve())] },
}

export const NoReadableFile: Story = {
  parameters: open(9999),
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('This chapter has no readable file.')).toBeVisible()
    await expect(await canvas.findByRole('link', { name: 'Back to library' })).toBeVisible()
  },
}

export const LoadFailed: Story = {
  parameters: {
    ...open(1132),
    msw: [http.get(api('/reader/chapter/:id'), () => failWith(500, 'The archive could not be opened'))],
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Could not load this chapter.')).toBeVisible()
    await expect(await canvas.findByRole('button', { name: 'Try again' })).toBeVisible()
  },
}

export const PageFailsToLoad: Story = {
  parameters: {
    ...open(4404),
    msw: [http.get(api('/reader/chapter/4404/page/:n'), () => new HttpResponse(null, { status: 500 }))],
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('This page failed to load.')).toBeVisible()
    await expect(await canvas.findByRole('button', { name: 'Try again' })).toBeVisible()
  },
}

/** Enhance Always from the resolved prefs: the page is drawn by the engine, and the toolbar's hold button shows the original. */
export const EnhanceAlways: Story = {
  parameters: {
    ...open(1132),
    msw: [
      withManifest({
        ...chapters[1132],
        prefs: { ...chapters[1132].prefs, enhance: 'always', enhanceMethod: 'a4k-s' },
      }),
    ],
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await canvas.findByRole('img', { name: 'Chapter 132 - page 6' })
    const { loadEnhanceEngine } = await import('../../lib/enhance')
    const engine = await loadEnhanceEngine()
    if (!engine.supported || engine.broken) return
    const enhanced = await waitFor(
      () => {
        const element = canvasElement.querySelector<HTMLCanvasElement>('canvas.reader-enhanced')
        expect(element?.dataset.enhanced).toBe('true')
        return element!
      },
      { timeout: 60_000 },
    )
    await expect(enhanced).toHaveAccessibleName('Chapter 132 - page 6')

    await summonChrome({ canvasElement, userEvent })
    const hold = await canvas.findByRole('button', { name: 'Hold to compare' })
    await userEvent.pointer({ keys: '[MouseLeft>]', target: hold })
    await waitFor(() => expect(hold).toHaveAttribute('aria-pressed', 'true'))
    await expect(enhanced).not.toBeVisible()
    await userEvent.pointer({ keys: '[/MouseLeft]', target: hold })
    await waitFor(() => expect(hold).toHaveAttribute('aria-pressed', 'false'))
    await waitFor(() => expect(enhanced).toBeVisible())
  },
}

const serverMethod = (
  chapterId: number,
  enhanceServer = chapters[1132].enhanceServer,
  prefs: Partial<ReaderPrefs> = {},
) =>
  withManifest({
    ...chapters[1132],
    chapterId,
    prefs: { ...chapters[1132].prefs, enhance: 'always', enhanceMethod: 'server:span-s', ...prefs },
    enhanceServer,
  })

/** Answers the window POST with one state for every page, and keeps what was asked for. */
const enhanceAnswer = (chapterId: number, state: ServerPageState, asked: number[][] = []) =>
  http.post(api(`/reader/chapter/${chapterId}/enhance`), async ({ request }) => {
    const { pages } = (await request.json()) as { pages: number[] }
    asked.push(pages)
    return HttpResponse.json({ pages: pages.map((page) => ({ page, state })) })
  })

async function engineWorks(): Promise<boolean> {
  const { loadEnhanceEngine } = await import('../../lib/enhance')
  const engine = await loadEnhanceEngine()
  return engine.supported && !engine.broken
}

/** Shows the browser fallback where the engine runs, else the plain page, and never a server image. */
async function expectFallback(canvasElement: HTMLElement, canvas: PlayArgs['canvas']) {
  const page = await canvas.findByRole('img', { name: 'Chapter 132 - page 6' })
  if (await engineWorks()) {
    await waitFor(
      () => expect(canvasElement.querySelector<HTMLCanvasElement>('canvas.reader-enhanced')?.dataset.enhanced).toBe('true'),
      { timeout: 60_000 },
    )
  } else {
    await expect(page).toBeVisible()
  }
  await expect(canvasElement.querySelector('img.reader-enhanced-server')).toBeNull()
}

const readyAsked: number[][] = []

/** SPAN-S with every page upscaled already: the server's image replaces the page once decoded, and compare still shows the original. */
export const ServerEnhanceReady: Story = {
  parameters: {
    ...open(7711),
    msw: [
      serverMethod(7711),
      enhanceAnswer(7711, 'ready', readyAsked),
      http.get(api('/reader/chapter/7711/page/:n/enhanced'), ({ params }) => HttpResponse.redirect(`/mock/page/7711/${Number(params.n) + 1}.svg`)),
    ],
  },
  play: async ({ canvas, canvasElement }) => {
    await canvas.findByRole('img', { name: 'Chapter 132 - page 6' })
    const server = await waitFor(() => {
      const element = canvasElement.querySelector<HTMLImageElement>('img.reader-enhanced-server')
      expect(element?.dataset.enhanced).toBe('true')
      return element!
    })
    await expect(server.getAttribute('src')).toContain('/reader/chapter/7711/page/5/enhanced?model=span-s&v=v1')
    await expect(server.naturalWidth).toBeGreaterThan(0)
    await waitFor(() => expect(readyAsked[0]?.[0]).toBe(5))
    await waitFor(() => expect(canvas.getByRole('img', { name: 'Chapter 132 - page 6' })).toBe(server))
    await expect(canvasElement.querySelector<HTMLCanvasElement>('canvas.reader-enhanced')?.dataset.enhanced).toBe('false')

    const original = canvasElement.querySelector<HTMLImageElement>('img.reader-region-image:not(.reader-enhanced-server)')!
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'h' }))
    await waitFor(() => expect(original).toBeVisible())
    await expect(server).not.toBeVisible()
    window.dispatchEvent(new KeyboardEvent('keyup', { key: 'h' }))
    await waitFor(() => expect(server).toBeVisible())
  },
}

const warmedPages = new Set<number>()

/** A page turn onto a page warmed ahead shows its server upscale in the first frame, never the fallback or the plain page. */
export const ServerEnhanceTurnShowsWarmedPage: Story = {
  parameters: {
    ...open(7717),
    msw: [
      serverMethod(7717),
      enhanceAnswer(7717, 'ready'),
      http.get(api('/reader/chapter/7717/page/:n/enhanced'), ({ params }) => {
        warmedPages.add(Number(params.n))
        return HttpResponse.redirect(`/mock/page/7717/${Number(params.n) + 1}.svg`)
      }),
    ],
  },
  play: async ({ canvas, canvasElement }) => {
    await canvas.findByRole('img', { name: 'Chapter 132 - page 6' })
    await waitFor(() =>
      expect(canvasElement.querySelector<HTMLImageElement>('img.reader-enhanced-server')?.dataset.enhanced).toBe('true'),
    )
    await waitFor(() => expect(warmedPages.has(6)).toBe(true))
    await new Promise((resolve) => setTimeout(resolve, 500))

    document.body.dispatchEvent(new KeyboardEvent('keydown', { key: ' ', bubbles: true }))
    // The old page stays up until the next one can paint, so the first page shown after the swap is
    // what the reader sees first.
    await waitFor(() =>
      expect(canvasElement.querySelector('img.reader-enhanced-server')?.getAttribute('src')).toContain('/page/6/'),
    )
    const server = canvasElement.querySelector<HTMLImageElement>('img.reader-enhanced-server')
    const plain = canvasElement.querySelector<HTMLImageElement>('img.reader-region-image:not(.reader-enhanced-server)')
    await expect(server?.getAttribute('src')).toContain('/reader/chapter/7717/page/6/enhanced')
    await expect(server?.dataset.enhanced).toBe('true')
    await expect(plain?.style.visibility).toBe('hidden')
    await expect(canvasElement.querySelector('canvas.reader-enhanced[data-enhanced="true"]')).toBeNull()
  },
}

const waitingAsked: number[][] = []

/** Still queued on the server and not in its cache: the reader asks for the window and shows the fallback meanwhile. */
export const ServerEnhanceWaiting: Story = {
  parameters: {
    ...open(7712),
    msw: [
      serverMethod(7712),
      enhanceAnswer(7712, 'queued', waitingAsked),
      http.get(api('/reader/chapter/7712/page/:n/enhanced'), () => new HttpResponse(null, { status: 404 })),
    ],
  },
  play: async ({ canvas, canvasElement }) => {
    await waitFor(() => expect(waitingAsked[0]?.slice(0, 2)).toEqual([5, 6]))
    await expectFallback(canvasElement, canvas)
    await waitFor(() => expect(canvasElement.querySelector('.reader-enhance-line')?.getAttribute('data-phase')).toBe('queued'))
    await expect(canvasElement.querySelector('.reader-enhance-status')).toBeNull()
    await expect(await canvas.findByText('Waiting for the server')).toBeInTheDocument()
  },
}

/** The detailed style: the labelled pill over the page instead of the edge line. */
export const ServerEnhanceWaitingDetailed: Story = {
  parameters: {
    ...open(7719),
    msw: [
      serverMethod(7719, undefined, { enhanceProgress: 'detailed' }),
      enhanceAnswer(7719, 'queued'),
      http.get(api('/reader/chapter/7719/page/:n/enhanced'), () => new HttpResponse(null, { status: 404 })),
    ],
  },
  play: async ({ canvasElement }) => {
    await waitFor(() => expect(canvasElement.querySelector('.reader-enhance-status')?.textContent).toContain('Waiting for the server'))
    await expect(canvasElement.querySelector('.reader-enhance-line')).toBeNull()
  },
}

/** Hidden: nothing over the page, the toolbar's compare button still shows the wait once the bars are up. */
export const ServerEnhanceWaitingHidden: Story = {
  parameters: {
    ...open(7720),
    msw: [
      serverMethod(7720, undefined, { enhanceProgress: 'off' }),
      enhanceAnswer(7720, 'queued'),
      http.get(api('/reader/chapter/7720/page/:n/enhanced'), () => new HttpResponse(null, { status: 404 })),
    ],
  },
  play: async ({ canvas, canvasElement }) => {
    await expect(await canvas.findByText('Waiting for the server')).toBeInTheDocument()
    await expect(canvasElement.querySelector('.reader-enhance-line')).toBeNull()
    await expect(canvasElement.querySelector('.reader-enhance-status')).toBeNull()
    await waitFor(() => expect(canvasElement.querySelector('.reader-compare')?.getAttribute('data-phase')).toBe('queued'))
    await expect(canvasElement.querySelector('.reader-compare-button')).toHaveAttribute('data-busy')
  },
}

const plainAsked: number[][] = []

/** Still queued, with the fallback set to None: the plain page shows until the server's upscale is ready. */
export const ServerEnhanceFallbackNone: Story = {
  parameters: {
    ...open(7716),
    msw: [
      serverMethod(7716, undefined, { enhanceFallback: 'off' }),
      enhanceAnswer(7716, 'queued', plainAsked),
      http.get(api('/reader/chapter/7716/page/:n/enhanced'), () => new HttpResponse(null, { status: 404 })),
    ],
  },
  play: async ({ canvas, canvasElement }) => {
    const page = await canvas.findByRole('img', { name: 'Chapter 132 - page 6' })
    await waitFor(() => expect(plainAsked[0]?.slice(0, 2)).toEqual([5, 6]))
    await new Promise((resolve) => setTimeout(resolve, 1000))
    await expect(page).toBeVisible()
    await expect(canvasElement.querySelector('canvas.reader-enhanced[data-enhanced="true"]')).toBeNull()
    await expect(canvasElement.querySelector('img.reader-enhanced-server')).toBeNull()
  },
}

const offAsked: number[][] = []

/** The admin switched the server tier off: the fallback stands in and nothing is asked of the server. */
export const ServerEnhanceTierOff: Story = {
  parameters: {
    ...open(7713),
    msw: [serverMethod(7713, { enabled: false, models: [] }), enhanceAnswer(7713, 'ready', offAsked)],
  },
  play: async ({ canvas, canvasElement }) => {
    await expectFallback(canvasElement, canvas)
    await new Promise((resolve) => setTimeout(resolve, 500))
    await expect(offAsked).toHaveLength(0)
  },
}

const volumeAsked: number[][] = []

/** SPAN-S on a digital volume, which the default prefs skip: the plain page shows and nothing is asked of the server. */
export const EnhanceSkippedForVolume: Story = {
  parameters: {
    ...open(7718),
    msw: [
      withManifest({
        ...chapters[1132],
        chapterId: 7718,
        tier: 'volume',
        prefs: { ...chapters[1132].prefs, enhance: 'always', enhanceMethod: 'server:span-s' },
      }),
      enhanceAnswer(7718, 'ready', volumeAsked),
    ],
  },
  play: async ({ canvas, canvasElement }) => {
    const page = await canvas.findByRole('img', { name: 'Chapter 132 - page 6' })
    await new Promise((resolve) => setTimeout(resolve, 800))
    await expect(page).toBeVisible()
    await expect(volumeAsked).toHaveLength(0)
    await expect(canvasElement.querySelector('canvas.reader-enhanced')).toBeNull()
  },
}

const unstretchedAsked: { pages: number[]; notWanted?: number[] }[] = []

/** Auto with SPAN-S and the page shown at 1:1: nothing is stretched, so no page is asked for and the pages measured plain go as `notWanted`, which drops what the previous chapter queued ahead. */
export const ServerEnhanceAutoUnstretched: Story = {
  parameters: {
    ...open(7714),
    msw: [
      serverMethod(7714, undefined, { enhance: 'auto', fit: 'original', scale: 100 }),
      http.post(api('/reader/chapter/7714/enhance'), async ({ request }) => {
        unstretchedAsked.push((await request.json()) as { pages: number[]; notWanted?: number[] })
        return HttpResponse.json({ pages: [] })
      }),
      http.get(api('/reader/chapter/7714/page/:n/enhanced'), () => new HttpResponse(null, { status: 404 })),
    ],
  },
  play: async ({ canvas, canvasElement }) => {
    await canvas.findByRole('img', { name: 'Chapter 132 - page 6' })
    await waitFor(() => expect(canvasElement.querySelector('canvas.reader-enhanced')).not.toBeNull())
    await waitFor(() => expect(unstretchedAsked.at(-1)?.notWanted).toContain(5))
    await new Promise((resolve) => setTimeout(resolve, 800))
    await expect(unstretchedAsked.flatMap((body) => body.pages)).toHaveLength(0)
  },
}

const wholeAsked: { pages: number[]; wholeChapter?: boolean }[] = []

/** Whole chapter: the window goes first, then every other page of the chapter, ahead before behind. */
export const ServerEnhanceWholeChapter: Story = {
  parameters: {
    ...open(7721),
    msw: [
      serverMethod(7721, undefined, { enhanceWholeChapter: true }),
      http.post(api('/reader/chapter/7721/enhance'), async ({ request }) => {
        const body = (await request.json()) as { pages: number[]; wholeChapter?: boolean }
        wholeAsked.push(body)
        return HttpResponse.json({ pages: body.pages.map((page) => ({ page, state: 'queued' })) })
      }),
      http.get(api('/reader/chapter/7721/page/:n/enhanced'), () => new HttpResponse(null, { status: 404 })),
    ],
  },
  play: async ({ canvas }) => {
    await canvas.findByRole('img', { name: 'Chapter 132 - page 6' })
    await waitFor(() => expect(wholeAsked.at(-1)?.pages).toHaveLength(18))
    await waitFor(() => expect(wholeAsked.at(-1)?.wholeChapter).toBe(true))
    await waitFor(() => expect(wholeAsked.at(-1)?.pages[0]).toBe(5))
    await waitFor(() => expect(wholeAsked.at(-1)?.pages.slice(-5)).toEqual([4, 3, 2, 1, 0]))
  },
}

const stretchedAsked: number[][] = []

/** Auto with SPAN-S at 200%: the page on screen is asked for first, then the pages ahead of the same size. */
export const ServerEnhanceAutoStretched: Story = {
  parameters: {
    ...open(7715),
    msw: [
      serverMethod(7715, undefined, { enhance: 'auto', fit: 'original', scale: 200 }),
      enhanceAnswer(7715, 'queued', stretchedAsked),
      http.get(api('/reader/chapter/7715/page/:n/enhanced'), () => new HttpResponse(null, { status: 404 })),
    ],
  },
  play: async ({ canvas }) => {
    await canvas.findByRole('img', { name: 'Chapter 132 - page 6' })
    await waitFor(() => expect(stretchedAsked[0]?.[0]).toBe(5))
    await waitFor(() => expect(stretchedAsked.flat()).toContain(6))
    await waitFor(() => expect(stretchedAsked.every((pages) => pages.length <= 12 && pages.every((page) => page >= 5))).toBe(true))
  },
}
