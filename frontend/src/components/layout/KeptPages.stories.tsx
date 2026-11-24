import { useState } from 'react'
import { Button, Modal } from '@mantine/core'
import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, userEvent, waitFor, within } from 'storybook/test'
import { Route, Routes, useNavigate } from 'react-router-dom'
import { KeptPages } from './KeptPages'

function MockDiscoverWithModal() {
  const [opened, setOpened] = useState(false)
  const navigate = useNavigate()
  return (
    <div>
      <h1>Discover</h1>
      <Button onClick={() => setOpened(true)}>Open detail modal</Button>
      <Modal
        opened={opened}
        onClose={() => setOpened(false)}
        title="Series details"
        zIndex={1000}
        transitionProps={{ duration: 200, transition: 'fade-down' }}
      >
        <Button
          onClick={() => {
            setOpened(false)
            // Navigate while the modal's 200ms exit transition is mid-flight, matching
            // DiscoverLibraryRail.goToLibrary + view-transition timing.
            window.setTimeout(() => navigate('/series/1'), 40)
          }}
        >
          Go to series
        </Button>
        <Button onClick={() => navigate('/series/1')}>Leave with modal open</Button>
      </Modal>
    </div>
  )
}

function MockSeriesPage() {
  const [clicks, setClicks] = useState(0)
  const navigate = useNavigate()
  return (
    <div>
      <h1>Series page</h1>
      <Button onClick={() => setClicks((c) => c + 1)}>Series action ({clicks})</Button>
      <Button onClick={() => navigate('/discover')}>Back to Discover</Button>
    </div>
  )
}

function Harness() {
  return (
    <>
      <KeptPages homeEnabled={false} Discover={MockDiscoverWithModal} />
      <Routes>
        <Route path="/discover/:tab?" element={null} />
        <Route path="/series/:id" element={<MockSeriesPage />} />
      </Routes>
    </>
  )
}

const meta = {
  title: 'Layout/KeptPages',
  component: Harness,
  tags: ['ai-generated'],
  parameters: {
    realMotion: true,
    router: { initialEntries: ['/discover'] },
  },
} satisfies Meta<typeof Harness>

export default meta
type Story = StoryObj<typeof meta>

export const ClosingModalDoesNotBlockNextRoute: Story = {
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    const body = within(canvasElement.ownerDocument.body)

    await userEvent.click(await canvas.findByRole('button', { name: 'Open detail modal' }))
    const dialog = await body.findByRole('dialog', { name: 'Series details' })
    await expect(dialog).toBeVisible()

    // Close and leave mid-exit-transition; the hidden kept page's portal must not swallow clicks
    // on the series page.
    await userEvent.click(within(dialog).getByRole('button', { name: 'Go to series' }))
    await canvas.findByRole('heading', { name: 'Series page' })

    const action = await canvas.findByRole('button', { name: 'Series action (0)' })
    await userEvent.click(action)
    await expect(await canvas.findByRole('button', { name: 'Series action (1)' })).toBeVisible()

    // Returning to Discover settles the interrupted exit transition cleanly.
    await userEvent.click(await canvas.findByRole('button', { name: 'Back to Discover' }))
    await canvas.findByRole('heading', { name: 'Discover' })
    await waitFor(() => expect(body.queryByRole('dialog', { name: 'Series details' })).toBeNull())
    await waitFor(() =>
      expect(canvasElement.ownerDocument.querySelector('.mantine-Modal-overlay')).toBeNull(),
    )
  },
}

export const OpenModalHidesWhileAway: Story = {
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    const body = within(canvasElement.ownerDocument.body)

    await userEvent.click(await canvas.findByRole('button', { name: 'Open detail modal' }))
    const dialog = await body.findByRole('dialog', { name: 'Series details' })
    await expect(dialog).toBeVisible()

    await userEvent.click(within(dialog).getByRole('button', { name: 'Leave with modal open' }))
    await canvas.findByRole('heading', { name: 'Series page' })

    const action = await canvas.findByRole('button', { name: 'Series action (0)' })
    await userEvent.click(action)
    await expect(await canvas.findByRole('button', { name: 'Series action (1)' })).toBeVisible()

    // Returning to Discover restores the still-open modal.
    await userEvent.click(await canvas.findByRole('button', { name: 'Back to Discover' }))
    await expect(await body.findByRole('dialog', { name: 'Series details' })).toBeVisible()
  },
}
