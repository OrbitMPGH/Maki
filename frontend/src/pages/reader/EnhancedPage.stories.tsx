import { useEffect, useState } from 'react'
import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor } from 'storybook/test'
import EnhancedPage from './EnhancedPage'
import { EnhanceScope, type PageEnhance } from './enhanceRuntime'
import { FULL_REGION } from './pageShape'
import type { ServerEnhance } from './useServerEnhance'

const SRC = '/mock/page/4401/1.svg'
const STANDBY: React.CSSProperties = { position: 'fixed', inset: 0, visibility: 'hidden', pointerEvents: 'none' }

/** Every set of pages the scope reported as wanted by Auto, in order. */
const reported: number[][] = []

const READY: ServerEnhance = {
  chapterId: 4401,
  model: 'maki-2x',
  pageVersion: 'v1',
  states: new Map([[0, 'ready']]),
  shown: new Set(),
  reportShown: () => {},
  reportMissing: () => {},
}

interface HarnessProps {
  /** Start in standby, shown by the "Show page" button. */
  standby?: boolean
  /** A second page of the same size, in a box that shows it stretched. */
  stretchedSibling?: boolean
  server?: ServerEnhance | null
}

function Harness({ standby: startInStandby = true, stretchedSibling = false, server = null }: HarnessProps) {
  const [image, setImage] = useState<HTMLImageElement | null>(null)
  const [box, setBox] = useState<HTMLDivElement | null>(null)
  const [siblingBox, setSiblingBox] = useState<HTMLDivElement | null>(null)
  const [standby, setStandby] = useState(startInStandby)
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
            server={server}
            priority={0}
            box={box}
            standby={standby}
            onShowing={() => {}}
          />
        )}
      </div>
      {stretchedSibling && (
        <div ref={setSiblingBox} style={{ position: 'relative', width: 1000 / devicePixelRatio, height: 1500 / devicePixelRatio }}>
          {image && (
            <EnhancedPage
              image={image}
              src={SRC}
              region={FULL_REGION}
              rotate={0}
              alt="Page 2"
              enhance={enhance}
              page={1}
              priority={1}
              box={siblingBox}
              onShowing={() => {}}
            />
          )}
        </div>
      )}
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

/**
 * Border crop trims each page by its own amount, so pages of one release shown near the threshold
 * fall on both sides of it. A page of the same size as one shown stretched is wanted too.
 */
export const SameSizeFollowsAStretchedPage: Story = {
  args: { standby: false, stretchedSibling: true },
  play: async () => {
    await waitFor(() => expect(reported.at(-1)).toEqual(expect.arrayContaining([0, 1])))
  },
}

/** A page Auto measured as plain still shows the server's upscale once it is ready: it costs nothing more. */
export const ReadyUpscaleShowsOnAPlainPage: Story = {
  args: { standby: false, server: READY },
  play: async ({ canvasElement }) => {
    const server = await waitFor(() => {
      const element = canvasElement.querySelector<HTMLImageElement>('img.reader-enhanced-server')
      expect(element?.dataset.enhanced).toBe('true')
      return element!
    })
    await expect(server.getAttribute('src')).toContain('/reader/chapter/4401/page/0/enhanced')
    await expect(reported.flat()).not.toContain(0)
  },
}
