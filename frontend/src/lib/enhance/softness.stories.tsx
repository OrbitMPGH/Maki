import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { measureBlur, SOFT_BLUR } from './softness'

/** A crisp block pattern, upscaled `factor` times with smoothing, as a release that was itself an upscale. */
async function upscaledPage(factor: number): Promise<HTMLImageElement> {
  const small = document.createElement('canvas')
  small.width = 600
  small.height = 900
  const context = small.getContext('2d')!
  context.fillStyle = '#fff'
  context.fillRect(0, 0, 600, 900)
  context.fillStyle = '#111'
  for (let y = 0; y < 900; y += 30) {
    for (let x = (y / 30) % 2 ? 0 : 15; x < 600; x += 30) context.fillRect(x, y, 3, 27)
  }
  const big = document.createElement('canvas')
  big.width = 600 * factor
  big.height = 900 * factor
  const scaled = big.getContext('2d')!
  scaled.imageSmoothingQuality = 'high'
  scaled.drawImage(small, 0, 0, big.width, big.height)
  const image = new Image()
  image.src = big.toDataURL('image/png')
  await image.decode()
  return image
}

const meta: Meta = { title: 'Lib/Enhance/Softness', render: () => <div /> }
export default meta

/** Soft at its own pixels, sharp at a quarter of them: Auto must not upscale a big page shown small. */
export const JudgedAtTheShownSize: StoryObj = {
  play: async () => {
    const crisp = await upscaledPage(1)
    const doubled = await upscaledPage(2)
    await expect(measureBlur(crisp)).toBeLessThan(SOFT_BLUR)
    await expect(measureBlur(doubled)).toBeGreaterThan(SOFT_BLUR)
    await expect(measureBlur(doubled, 0.25)).toBeLessThan(SOFT_BLUR)
    await expect(measureBlur(doubled, 1.5)).toBe(measureBlur(doubled))
  },
}
