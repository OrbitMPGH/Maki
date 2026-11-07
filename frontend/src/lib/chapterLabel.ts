import { serverChapterLabel } from '../format'

// A cover chip has room for the chapter only, and the chapter number alone already places it; the
// full label stays on the chip's tooltip.
export function coverChapterLabel(label: string): string {
  return serverChapterLabel(label, false)
}
