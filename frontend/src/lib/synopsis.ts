// MangaBaka synopses carry Markdown source, but the app renders them as plain text. This unpicks
// the few forms that show up (escapes, emphasis, links). Not a Markdown renderer.

// Escaped punctuation is swapped for a private-use code point so the emphasis passes never see it.
const ESCAPE_BASE = 0xe000
const ESCAPABLE = /\\([\\`*_{}[\]()#+\-.!~>])/g
const ESCAPE_PLACEHOLDER = new RegExp(`[${String.fromCharCode(ESCAPE_BASE)}-${String.fromCharCode(ESCAPE_BASE + 0xff)}]`, 'g')
const URL_TOKEN = /\S+:\/\/\S+/u

export function cleanSynopsis(text: string | null | undefined): string | null | undefined {
  if (!text) return text

  let result = text.replace(ESCAPABLE, (_m, ch: string) => String.fromCharCode(ESCAPE_BASE + ch.charCodeAt(0)))

  // [label](url) -> label, allowing one level of balanced parens in the URL.
  result = result.replace(/\[([^\]]+)\]\(((?:[^()]|\([^()]*\))+)\)/g, '$1')

  result = result.replace(/\*\*([\s\S]+?)\*\*/g, '$1')
  result = result.replace(/__([^_\n]+)__/g, '$1')

  // *italic*, possibly spanning lines: no space inside the markers, no word character or asterisk
  // outside, so "f*ck", "5*" and "1 * 2 * 3" survive.
  result = result.replace(/(^|[^\w*])\*(?!\s)([^*]+?)(?<!\s)\*(?![\w*])/gu, '$1$2')

  // _italic_ with Unicode boundaries, skipping URL tokens.
  result = result
    .split(new RegExp(`(${URL_TOKEN.source})`, 'u'))
    .map((part) =>
      URL_TOKEN.test(part)
        ? part
        : part.replace(/(^|[^\p{L}\p{N}_])_(?!\s)([^_\n]+?)(?<!\s)_(?![\p{L}\p{N}_])/gu, '$1$2'),
    )
    .join('')

  return result.replace(ESCAPE_PLACEHOLDER, (m) => String.fromCharCode(m.charCodeAt(0) - ESCAPE_BASE))
}

const SOURCE_LINE = /^\s*[([]?\s*Source\s*:\s*([^()[\]\n]+?)\s*[)\]]?\s*$/i
const NOTE_START = /^\s*\**\s*Notes?\s*:\**\s*(.*)$/i
const NOTE_ITEM = /^\s*[-•]\s*(.*)$/
const SOURCE_TAIL = /\s*[([]\s*Source\s*:\s*([^()[\]\n]+?)\s*[)\]]\s*$/i
const EMPTY_NOTES_TAIL = /\s*\**\s*Notes?\s*:[\s*-]*$/i
const stripStars = (s: string) => s.replace(/^[\s*]+|[\s*]+$/g, '')

export interface SynopsisParts {
  body: string
  notes: string[]
  source: string | null
}

/**
 * A cleaned synopsis split for display; the stored text is untouched. A trailing "Notes:" block
 * comes out as `notes` (one entry per "- " item, dropped when empty) and a "(Source: X)" ending the
 * text, or just before those notes, as `source` for a credit line. The rest stays in `body`.
 */
export function splitSynopsis(text: string | null | undefined): SynopsisParts {
  if (!text) return { body: text ?? '', notes: [], source: null }

  let lines = text.replace(EMPTY_NOTES_TAIL, '').split(/\r?\n/)
  let notes: string[] = []
  const n = lines.findLastIndex((line) => NOTE_START.test(line))
  if (n > 0) {
    const items = lines.slice(n + 1).filter((line) => line.trim() !== '')
    if (items.every((line) => NOTE_ITEM.test(line))) {
      const first = stripStars(NOTE_START.exec(lines[n])?.[1] ?? '')
      notes = [first, ...items.map((line) => stripStars(NOTE_ITEM.exec(line)?.[1] ?? ''))].filter(
        (note) => note !== '' && note !== '-',
      )
      lines = lines.slice(0, n)
    }
  }

  while (lines.length > 0 && lines[lines.length - 1].trim() === '') lines.pop()
  let source: string | null = null
  const last = lines.length > 0 ? lines[lines.length - 1] : ''
  const standalone = SOURCE_LINE.exec(stripStars(last))
  const inline = standalone ? null : SOURCE_TAIL.exec(last)
  if (standalone) {
    source = standalone[1]
    lines = lines.slice(0, -1)
  } else if (inline) {
    source = inline[1]
    lines = [...lines.slice(0, -1), last.slice(0, inline.index)]
  }

  const body = lines.join('\n').trim()
  return body ? { body, notes, source } : { body: text, notes: [], source: null }
}
