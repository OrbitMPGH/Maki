import { useEffect } from 'react'
import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { getConnectivity, onConnectivityChange, setConnectivity } from '../lib/connectivity'
import { OfflineBanner } from './OfflineBanner'

// Every API response flips connectivity back to online, so the offline story pins it for as long as
// it is mounted. The banner only renders in a secure context with service workers, which the test
// browser is on localhost.
function ForceOffline({ children }: { children: React.ReactNode }) {
  useEffect(() => {
    const pin = () => {
      if (getConnectivity() === 'online') setConnectivity('offline')
    }
    pin()
    const stop = onConnectivityChange(pin)
    return () => {
      stop()
      setConnectivity('online')
    }
  }, [])
  return <>{children}</>
}

const meta = {
  component: OfflineBanner,
  tags: ['ai-generated'],
  parameters: { layout: 'padded' },
} satisfies Meta<typeof OfflineBanner>

export default meta
type Story = StoryObj<typeof meta>

export const Offline: Story = {
  decorators: [
    (Story) => (
      <ForceOffline>
        <Story />
      </ForceOffline>
    ),
  ],
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('status')).toHaveTextContent("You're offline")
    await expect(canvas.getByRole('link', { name: 'Offline reading' })).toHaveAttribute(
      'href',
      '/settings?tab=reading&s=offline-reading',
    )
  },
}

export const Online: Story = {
  play: async ({ canvas }) => {
    await expect(canvas.queryByRole('status')).toBeNull()
  },
}
