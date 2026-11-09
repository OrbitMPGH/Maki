import { useEffect, useState } from 'react'
import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor } from 'storybook/test'
import EnhancedPage from './EnhancedPage'
import { EnhanceScope, type PageEnhance } from './enhanceRuntime'
import { FULL_REGION } from './pageShape'

const SRC = '/mock/page/4401/1.svg'
const STANDBY: React.CSSProperties = { position: 'fixed', inset: 0, visibility: 'hidden', pointerEvents: 'none' }

/** Every set of pages the scope reported as wanted by Auto, in order. */
const reported: number[][] = []

function Harness() {
  const [image, setImage] = useState<HTMLImageElement | null>(null)
  const [box, setBox] = useState<HTMLDivElement | null>(null)
  const [standby, setStandby] = useState(true)
  const [enhance] = useState<PageEnhance>(() => ({
    mode: 'auto',
    preset: null,
    engine: null,
    scope: new EnhanceScope(undefined, (pages) => reported.push([...pages])),
    layout: 'contain',
  }))

  useEffect(() => {
    const element = new Image()
    element.src = SRC
    void element.decode().then(() => setImage(element))
  }, [])

  return (
    <>
      <button type="button" onClick={() => setStandby(false)}>
        Show page
      </button>
      <div ref={setBox} style={standby ? STANDBY : { position: 'relative', width: 400, height: 600 }}>
        {image && (
          <EnhancedPage
            image={image}
            src={SRC}
            region={FULL_REGION}
            rotate={0}
            alt="Page 1"
            enhance={enhance}
            page={0}
            priority={0}
            box={box}
            standby={standby}
            onShowing={() => {}}
          />
        )}
      </div>
    </>
  )
}

const meta = {
  title: 'pages/reader/EnhancedPage',
  component: Harness,
  beforeEach: () => {
    reported.length = 0
  },
} satisfies Meta<typeof Harness>

export default meta
type Story = StoryObj<typeof meta>

/**
 * An 800x1200 page waiting for a turn sits in a box that fills the viewport. Measured there it would
 * count as stretched on Auto and be sent to the server, then dropped once the real 400x600 box is measured.
 */
export const StandbyIsNotMeasured: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await waitFor(() => expect(canvasElement.querySelector('canvas.reader-enhanced')).not.toBeNull())
    await new Promise((resolve) => setTimeout(resolve, 500))
    await expect(reported).toHaveLength(0)

    await userEvent.click(await canvas.findByRole('button', { name: 'Show page' }))
    await waitFor(() => expect(reported.length).toBeGreaterThan(0))
    await new Promise((resolve) => setTimeout(resolve, 500))
    await expect(reported.flat()).not.toContain(0)
  },
}
