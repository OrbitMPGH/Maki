import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith, neverResolve } from '../../../.storybook/mocks/api'
import { chapters } from '../../../.storybook/mocks/entry/fixtures'
import type { ReaderManifest } from '../../api/reader'
import ReaderPage from './ReaderPage'

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
    await expect(await body.findByText('Reading profile')).toBeVisible()
    await waitFor(() => expect(body.getByText(/^Changes /)).toHaveTextContent('Changes retune'))
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
