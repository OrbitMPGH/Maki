import { useState, type ReactNode } from 'react'
import { useLingui } from '@lingui/react/macro'
import { pressable } from '../../lib/pressable'

const URL_PATTERN = /https?:\/\/[^\s<>"']+/g
const SPOILER_PATTERN = /\|\|(.*?)\|\|/gs
const TRAILING_PUNCTUATION = /[.,;:!?)\]]+$/

/** Plain text with `http(s)` links. Everything stays a text node, so there is nothing to escape. */
function linkify(text: string, keyPrefix: string): ReactNode[] {
  const parts: ReactNode[] = []
  let last = 0
  for (const match of text.matchAll(URL_PATTERN)) {
    const start = match.index
    let url = match[0]
    const trailing = TRAILING_PUNCTUATION.exec(url)
    if (trailing) url = url.slice(0, url.length - trailing[0].length)
    if (url.length <= 'https://'.length) continue
    if (start > last) parts.push(text.slice(last, start))
    parts.push(
      <a key={`${keyPrefix}-${start}`} href={url} target="_blank" rel="noopener noreferrer nofollow">
        {url}
      </a>,
    )
    last = start + url.length
  }
  if (last < text.length) parts.push(text.slice(last))
  return parts
}

function Spoiler({ children }: { children: ReactNode }) {
  const { t } = useLingui()
  const [revealed, setRevealed] = useState(false)
  if (revealed) return <span className="note-spoiler" data-revealed>{children}</span>
  return (
    <span
      className="note-spoiler"
      aria-label={t`Spoiler, press to reveal`}
      {...pressable(() => setRevealed(true))}
    >
      <span className="note-spoiler-content" aria-hidden>
        {children}
      </span>
    </span>
  )
}

/**
 * A note's text. The whole format is line breaks, `http(s)` links and `||spoiler||`, which keeps
 * markup out by construction: nothing here ever reaches the DOM as HTML.
 */
export default function NoteBody({ text }: { text: string }) {
  const nodes: ReactNode[] = []
  let last = 0
  for (const match of text.matchAll(SPOILER_PATTERN)) {
    const start = match.index
    if (start > last) nodes.push(...linkify(text.slice(last, start), `t${last}`))
    nodes.push(<Spoiler key={`s${start}`}>{match[1] === '' ? '    ' : linkify(match[1], `s${start}`)}</Spoiler>)
    last = start + match[0].length
  }
  if (last < text.length) nodes.push(...linkify(text.slice(last), `t${last}`))
  return <div className="note-body">{nodes}</div>
}
