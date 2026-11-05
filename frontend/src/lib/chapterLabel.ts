// The server renders "Vol.3 Ch.12" (ChapterLabel.cs). A cover chip has room for the chapter only, and
// the chapter number alone already places it; the full label stays on the chip's tooltip.
export function coverChapterLabel(label: string): string {
  return label.replace(/^Vol\.\d+\s+(?=Ch\.)/, '')
}
