import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { layerHandlers } from '../../.storybook/mocks/shell/use'
import { downloadingDump, rootFolderFixtures, wizardHandlers, type WizardOptions } from '../../.storybook/mocks/shell/wizard'
import SetupWizard from './SetupWizard'

// Writes in the wizard stick for one story run, so each run builds its own set of handlers.
const wizard = (options: WizardOptions = {}) => layerHandlers(() => wizardHandlers(options))

const meta = {
  component: SetupWizard,
  tags: ['ai-generated'],
  parameters: { layout: 'fullscreen', router: { initialEntries: ['/'] } },
  beforeEach: wizard(),
} satisfies Meta<typeof SetupWizard>

export default meta
type Story = StoryObj<typeof meta>

const TITLES = [
  'Welcome to Maki',
  'Where your manga lives',
  'Metadata and Discover',
  'What to download',
  'Tools you already run',
  "You're set",
]

type Body = ReturnType<typeof within>

// The guide is a full screen modal, so everything is queried from document.body.
const bodyOf = (canvasElement: HTMLElement): Body => within(canvasElement.ownerDocument.body)

async function openStep(body: Body, userEvent: { click: (el: Element) => Promise<void> }, step: number) {
  await body.findByRole('heading', { name: TITLES[0] })
  for (let i = 1; i <= step; i++) {
    await userEvent.click(await body.findByRole('button', { name: 'Continue' }))
    await body.findByRole('heading', { name: TITLES[i] })
  }
}

export const Welcome: Story = {
  play: async ({ canvasElement }) => {
    const body = bodyOf(canvasElement)
    await expect(await body.findByRole('dialog')).toBeVisible()
    await expect(await body.findByRole('heading', { name: 'Welcome to Maki' })).toBeVisible()
    await waitFor(() => expect(body.getAllByText('Step 1 of 6').length).toBeGreaterThan(0))
    await expect(await body.findByRole('button', { name: 'Continue' })).toBeVisible()
    await expect(await body.findByRole('radiogroup', { name: 'Background' })).toBeInTheDocument()
  },
}

export const WelcomeOnPhone: Story = {
  parameters: { viewport: { defaultViewport: 'mobile1' } },
  globals: { viewport: { value: 'mobile1', isRotated: false } },
  play: async ({ canvasElement }) => {
    await expect(await bodyOf(canvasElement).findByRole('heading', { name: 'Welcome to Maki' })).toBeVisible()
  },
}

export const LibraryWithoutFolders: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const body = bodyOf(canvasElement)
    await openStep(body, userEvent, 1)
    await expect(await body.findByRole('textbox', { name: 'Folder path' })).toBeVisible()
    await waitFor(() => expect(body.getByRole('button', { name: 'Add folder' })).toBeDisabled())
    // The rail flags the missing folder in its hint.
    await expect(await body.findByText('No folder yet')).toBeVisible()
  },
}

export const LibraryAddFolder: Story = {
  beforeEach: wizard({ folders: [rootFolderFixtures[0]] }),
  play: async ({ canvasElement, userEvent }) => {
    const body = bodyOf(canvasElement)
    await openStep(body, userEvent, 1)
    await expect(await body.findByText('/library/manga')).toBeVisible()
    await userEvent.type(await body.findByRole('textbox', { name: 'Folder path' }), '/mnt/new-shelf')
    await userEvent.click(await body.findByRole('button', { name: 'Add folder' }))
    await expect(await body.findByText('/mnt/new-shelf')).toBeVisible()
    await waitFor(() => expect(body.getByRole('textbox', { name: 'Folder path' })).toHaveValue(''))
  },
}

export const LibraryRemoveFolder: Story = {
  beforeEach: wizard({ folders: rootFolderFixtures }),
  play: async ({ canvasElement, userEvent }) => {
    const body = bodyOf(canvasElement)
    await openStep(body, userEvent, 1)
    await body.findByText('/mnt/archive/manhwa')
    await userEvent.click((await body.findAllByRole('button', { name: 'Remove root folder' }))[1])
    const dialog = await body.findByRole('dialog', { name: 'Remove this root folder?' })
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Remove folder' }))
    await waitFor(() => expect(body.queryByText('/mnt/archive/manhwa')).toBeNull())
    await expect(await body.findByText('/library/manga')).toBeVisible()
  },
}

export const LibraryUnreachableFolder: Story = {
  beforeEach: wizard({
    folders: [rootFolderFixtures[0], { id: 3, path: '/mnt/offline-nas/manga', freeSpace: null, accessible: false }],
  }),
  play: async ({ canvasElement, userEvent }) => {
    const body = bodyOf(canvasElement)
    await openStep(body, userEvent, 1)
    await expect(await body.findByText("Maki can't reach this folder")).toBeVisible()
  },
}

export const DiscoveryStep: Story = {
  beforeEach: wizard({ metadata: { dumpPresent: true } }),
  play: async ({ canvasElement, userEvent }) => {
    const body = bodyOf(canvasElement)
    await openStep(body, userEvent, 2)
    await expect(await body.findByText('Metadata database ready')).toBeVisible()
    await expect(await body.findByText('Database ready')).toBeVisible()
    await expect(body.queryByRole('switch', { name: /local copy/i })).toBeNull()
  },
}

export const DiscoveryWhileDownloading: Story = {
  beforeEach: wizard({ dump: downloadingDump }),
  play: async ({ canvasElement, userEvent }) => {
    const body = bodyOf(canvasElement)
    await openStep(body, userEvent, 2)
    await expect(await body.findByRole('progressbar', { name: 'Downloading metadata database' })).toBeVisible()
  },
}

export const DiscoveryRecommendationsOff: Story = {
  beforeEach: wizard({ recommendations: { embeddingModel: 'off', vectorCount: 0 } }),
  play: async ({ canvasElement, userEvent }) => {
    const body = bodyOf(canvasElement)
    await openStep(body, userEvent, 2)
    const toggle = await body.findByRole('switch', { name: /Semantic search and recommendations/ })
    await waitFor(() => expect(toggle).not.toBeChecked())
    await userEvent.click(toggle)
    await waitFor(() =>
      expect(body.getByRole('switch', { name: /Semantic search and recommendations/ })).toBeChecked(),
    )
  },
}

export const DownloadsStep: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const body = bodyOf(canvasElement)
    await openStep(body, userEvent, 3)
    await expect(await body.findByText('Languages to download')).toBeVisible()
    const specials = await body.findByRole('switch', { name: 'Skip specials on new series' })
    await userEvent.click(specials)
    await waitFor(() => expect(body.getByRole('switch', { name: 'Skip specials on new series' })).toBeChecked())
  },
}

export const ConnectionsStep: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const body = bodyOf(canvasElement)
    await openStep(body, userEvent, 4)
    const flare = await body.findByRole('button', { name: /FlareSolverr/ })
    await waitFor(() => expect(flare).toHaveAttribute('aria-expanded', 'false'))
    await userEvent.click(flare)
    await waitFor(() => expect(flare).toHaveAttribute('aria-expanded', 'true'))
    await waitFor(() => expect(body.getAllByText('Not set')).toHaveLength(4))
  },
}

export const ConnectionsSaved: Story = {
  beforeEach: wizard({
    connections: {
      flaresolverr: { url: 'http://192.168.1.10:8191' },
      kavita: { url: 'http://192.168.1.10:5000', apiKey: 'test-key', pathMapFrom: null, pathMapTo: null },
    },
  }),
  play: async ({ canvasElement, userEvent }) => {
    const body = bodyOf(canvasElement)
    await openStep(body, userEvent, 4)
    await waitFor(() => expect(body.getAllByText('Saved')).toHaveLength(2))
    await expect(await body.findByText('2 connected')).toBeVisible()
  },
}

export const UnsavedConnectionWarning: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const body = bodyOf(canvasElement)
    await openStep(body, userEvent, 4)
    const flare = await body.findByRole('button', { name: /FlareSolverr/ })
    await userEvent.click(flare)
    const panel = document.getElementById(flare.getAttribute('aria-controls') ?? '')
    if (!panel) throw new Error('FlareSolverr panel not found')
    await userEvent.type(await within(panel).findByLabelText('URL'), 'http://localhost:8191')
    // The first Continue only asks; the second goes through and drops the typed value.
    await userEvent.click(await body.findByRole('button', { name: 'Continue' }))
    await waitFor(() => expect(body.getByRole('status')).toHaveTextContent('A connection has unsaved changes.'))
    await userEvent.click(await body.findByRole('button', { name: 'Continue without saving' }))
    await expect(await body.findByRole('heading', { name: "You're set" })).toBeVisible()
  },
}

export const FinishWithoutFolders: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const body = bodyOf(canvasElement)
    await openStep(body, userEvent, 5)
    await expect(await body.findByText('No library folder yet. Series need one to download into.')).toBeVisible()
    await userEvent.click(await body.findByRole('button', { name: 'Add one' }))
    await expect(await body.findByRole('heading', { name: 'Where your manga lives' })).toBeVisible()
  },
}

export const FinishAllSet: Story = {
  beforeEach: wizard({
    folders: rootFolderFixtures,
    metadata: { dumpPresent: true },
    connections: { flaresolverr: { url: 'http://192.168.1.10:8191' } },
  }),
  play: async ({ canvasElement, userEvent }) => {
    const body = bodyOf(canvasElement)
    await openStep(body, userEvent, 5)
    await expect(await body.findByText('2 library folders')).toBeVisible()
    await expect(await body.findByText('Local MangaBaka database ready')).toBeVisible()
    await expect(await body.findByText('Connected: FlareSolverr')).toBeVisible()
    await expect(await body.findByText('Where to go next')).toBeVisible()
  },
}

/** The database is required, so Finish and the next-step cards wait for it; Skip still leaves. */
export const FinishWaitsForDatabase: Story = {
  beforeEach: wizard({ folders: [rootFolderFixtures[0]], dump: downloadingDump }),
  play: async ({ canvasElement, userEvent }) => {
    const body = bodyOf(canvasElement)
    await openStep(body, userEvent, 5)
    await waitFor(() => expect(body.getByRole('button', { name: 'Finish setup' })).toBeDisabled())
    await expect(await body.findByText('Waiting for the metadata database.')).toBeVisible()
    await waitFor(() => expect(body.getByRole('button', { name: /Add a series/ })).toBeDisabled())
    await userEvent.click(await body.findByRole('button', { name: 'See progress' }))
    await expect(await body.findByRole('progressbar', { name: 'Downloading metadata database' })).toBeVisible()
  },
}

export const FinishSetup: Story = {
  beforeEach: wizard({ folders: [rootFolderFixtures[0]], metadata: { dumpPresent: true } }),
  play: async ({ canvasElement, userEvent }) => {
    const body = bodyOf(canvasElement)
    await openStep(body, userEvent, 5)
    await userEvent.click(await body.findByRole('button', { name: 'Finish setup' }))
    // Completing the setup closes the guide.
    await waitFor(() => expect(body.queryByRole('dialog')).toBeNull())
  },
}

export const SkipSetup: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const body = bodyOf(canvasElement)
    await body.findByRole('heading', { name: 'Welcome to Maki' })
    await userEvent.click((await body.findAllByRole('button', { name: 'Skip setup' }))[0])
    await waitFor(() => expect(body.queryByRole('dialog')).toBeNull())
  },
}

export const JumpFromTheRail: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const body = bodyOf(canvasElement)
    await body.findByRole('heading', { name: 'Welcome to Maki' })
    const rail = within(await body.findByRole('navigation', { name: 'Setup steps' }))
    await userEvent.click(await rail.findByRole('button', { name: /Downloads/ }))
    await expect(await body.findByRole('heading', { name: 'What to download' })).toBeVisible()
    await waitFor(() => expect(rail.getByRole('button', { name: /Downloads/ })).toHaveAttribute('aria-current', 'step'))
  },
}

export const AlreadyCompleted: Story = {
  beforeEach: wizard({ completed: true }),
  play: async ({ canvasElement }) => {
    // Give the setup request time to answer before checking that nothing opened.
    await new Promise((resolve) => setTimeout(resolve, 150))
    await expect(bodyOf(canvasElement).queryByRole('dialog')).toBeNull()
  },
}
