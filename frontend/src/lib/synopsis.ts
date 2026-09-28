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
