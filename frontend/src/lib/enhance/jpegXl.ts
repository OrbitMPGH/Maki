/**
 * Whether this browser decodes JPEG XL: Chrome 155+, Firefox 158+ and Safari 17+ do, Edge does not.
 * The server keeps upscaled colour pages as JPEG XL and converts them to WebP for a URL without
 * `jxl=1`, so the answer only decides which of the two this tab is sent.
 */
const PROBE = 'data:image/jxl;base64,/woAEBAJCAABADQASxiLFcJJwuUB9oIMBA=='

let known: boolean | null = null
let pending: Promise<boolean> | null = null

async function decodes(): Promise<boolean> {
  if (typeof Image === 'undefined') return false
  try {
    const image = new Image()
    image.src = PROBE
    await image.decode()
    return image.naturalWidth === 1
  } catch {
    return false
  }
}

export function jpegXlSupported(): Promise<boolean> {
  pending ??= decodes().then((supported) => (known = supported))
  return pending
}

/** The answer once the probe has settled, or null before that. */
export function jpegXlSupportedNow(): boolean | null {
  return known
}
