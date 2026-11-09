import { useLayoutEffect, useRef, useState, type CSSProperties, type FocusEvent } from 'react'
import { ActionIcon, Tooltip } from '@mantine/core'
import { IconCheck, IconCopy } from '../../lib/icons'
import { useLingui } from '@lingui/react/macro'
import { useCopyText } from './useCopyText'

const NUMBER_TOKEN = /(?<=^|[\s_\-[(./\\])(?:vol(?:ume)?\.?|ch(?:apter)?\.?|v|c)\s?\d/gi
const MAX_TAIL = 32
const MAX_LONG_TAIL = 48

/**
 * What stays when the middle goes. Release names put the volume or chapter number in the middle
 * and tags at the end, so the tail starts at the first number token that leaves at most 32
 * characters ("v01 (2022) (Digital).cbz"). Failing that, the last token that leaves at most 48,
 * so one more tag ("(f2)") does not swap the volume for the extension and leave a column of
 * names that differ only where they are cut. Without one it is the last segment of a path, so a
 * folder keeps its own name ("X:\library\...\Series"), then the extension and eight characters
 * before it, or the last six characters of an id.
 */
export function splitForTruncation(value: string): [head: string, tail: string] {
  let longTail = -1
  for (const m of value.matchAll(NUMBER_TOKEN)) {
    if (m.index > 0 && value.length - m.index <= MAX_TAIL) return [value.slice(0, m.index), value.slice(m.index)]
    if (m.index > 0 && value.length - m.index <= MAX_LONG_TAIL) longTail = m.index
  }
  if (longTail > 0) return [value.slice(0, longTail), value.slice(longTail)]
  const lastSeparator = Math.max(value.lastIndexOf('/'), value.lastIndexOf('\\'))
  if (value.length > 24 && lastSeparator > 0 && lastSeparator < value.length - 1 && value.length - lastSeparator <= MAX_LONG_TAIL) {
    return [value.slice(0, lastSeparator), value.slice(lastSeparator)]
  }
  const ext = /\.[A-Za-z0-9]{1,5}$/.exec(value)?.[0] ?? ''
  const keep = ext ? ext.length + 8 : 6
  if (value.length <= Math.max(keep + 4, 16)) return [value, '']
  return [value.slice(0, -keep), value.slice(-keep)]
}

/**
 * How much of a column's names is the same leading text ("Call of the Night "), cut back to a word
 * boundary so no word is split. That part goes first when the names lose their middle, so the
 * volume and tags that tell rows apart stay. 0 when fewer than two names or under eight characters.
 */
export function sharedPrefixLength(values: string[]): number {
  if (values.length < 2) return 0
  let n = Math.min(...values.map((v) => v.length))
  for (const v of values) {
    let i = 0
    while (i < n && v[i] === values[0][i]) i++
    n = i
  }
  const prefix = values[0].slice(0, n)
  const boundary = Math.max(...[' ', '_', '-', '.', '/', '\\'].map((c) => prefix.lastIndexOf(c))) + 1
  return boundary >= 8 ? boundary : 0
}

/**
 * A shared prefix too long for its room, in at most `room` characters: cut after a whole word
 * ("Call of the… "), or a bare "…" when that would keep less than half of it.
 */
export function cutSharedPrefix(prefix: string, room: number): string {
  const words = prefix.replace(/[\s_.-]+$/, '')
  const joiner = prefix.slice(words.length)
  if (words.length + joiner.length <= room) return prefix
  let keep = 0
  for (const m of words.matchAll(/[\s_.-]+/g)) {
    if (m.index + 1 + joiner.length <= room) keep = m.index
  }
  return keep >= words.length / 2 ? `${words.slice(0, keep)}…${joiner}` : '…'
}

// One observer for every value on the page: a file table holds hundreds of them.
const clipWatchers = new WeakMap<Element, () => void>()
let clipObserver: ResizeObserver | undefined
function watchSize(el: Element, onResize: () => void) {
  if (typeof ResizeObserver === 'undefined') {
    onResize()
    return () => {}
  }
  clipObserver ??= new ResizeObserver((entries) => {
    for (const entry of entries) clipWatchers.get(entry.target)?.()
  })
  clipWatchers.set(el, onResize)
  clipObserver.observe(el)
  return () => {
    clipObserver?.unobserve(el)
    clipWatchers.delete(el)
  }
}

/**
 * A machine string (file name, path, source id) on one line. When its column is too narrow it loses
 * its middle, so the start and the extension stay readable; the full value is in a tooltip and,
 * with `copy`, one click away. With `copy` the button is the single tab stop and shows the tooltip
 * on focus; without it the clipped value itself is focusable. Unless `wrap` is set it never wraps, so a table holding one needs a min width and scrolls
 * as a whole. `full` is what the tooltip shows and the button copies when the label is shorter, a
 * file name standing for its path. `minWidth` stops a table column collapsing to the tail.
 */
export function MonoValue({
  value,
  full,
  href,
  copy,
  focusable = true,
  minWidth,
  tailCh,
  splitAt,
  wrap,
  fz,
  c,
}: {
  value: string
  full?: string
  href?: string
  copy?: boolean
  /** False when the row already has a control to tab to and hover, the title and the DOM text are enough. */
  focusable?: boolean
  minWidth?: CSSProperties['minWidth']
  /** Characters kept for the tail, the longest tail in the column, so every row loses its middle at the same place. */
  tailCh?: number
  /** Where the tail starts, overriding the guess, e.g. after a prefix every row in the column shares. */
  splitAt?: number
  /** Wraps onto more lines, between path segments, instead of losing its middle, for a cell with height to spare. */
  wrap?: boolean
  fz?: CSSProperties['fontSize']
  c?: CSSProperties['color']
}) {
  const { t } = useLingui()
  const { copied, copy: copyValue } = useCopyText()
  const headRef = useRef<HTMLSpanElement>(null)
  const [opened, setOpened] = useState(false)
  const [clipped, setClipped] = useState(false)
  const [cut, setCut] = useState<string | null>(null)
  const whole = full ?? value
  const shared = !wrap && !!splitAt && splitAt < value.length
  const [head, tail] = wrap
    ? [value, '']
    : shared
      ? [value.slice(0, splitAt), value.slice(splitAt)]
      : splitForTruncation(value)
  // Clipped, or standing for something longer: only then is there anything to reveal. The value
  // takes focus for that only when it has no copy button, which reveals the same tooltip on focus,
  // so a table of values has one tab stop per row. The full text is in the DOM either way.
  const hidesSome = whole !== value || clipped

  useLayoutEffect(() => {
    const el = headRef.current
    const box = el?.parentElement
    if (!el || !box) return
    const over = (e: Element | null) => !!e && e.scrollWidth > e.clientWidth
    return watchSize(box, () => {
      const headClipped = over(el)
      setClipped(headClipped || over(el.nextElementSibling))
      // A shared prefix cut mid-word reads as a mangled title, so it is drawn over the real text
      // at a word or as a bare ellipsis. The text itself keeps the layout, so the room stays put.
      const head = el.textContent ?? ''
      const room = headClipped && el.clientWidth > 0 ? Math.floor(el.clientWidth / (el.scrollWidth / head.length)) : 0
      setCut(shared && headClipped ? cutSharedPrefix(head, room) + (el.nextElementSibling?.textContent ?? '') : null)
    })
  }, [value, shared])

  const show = () => setOpened(hidesSome)
  // A click focuses the copy button too, and the pointer is not on the text to take the tooltip
  // back down, so only keyboard focus opens it.
  const showOnKeyboardFocus = (e: FocusEvent<HTMLElement>) => {
    if (e.currentTarget.matches(':focus-visible')) show()
  }
  const hide = () => setOpened(false)

  const text = (
    <>
      <span ref={headRef} className="mono-value-head" data-split={tail ? true : undefined}>
        {wrap
          ? head.split(/(?<=[/\\])/).map((segment, i) => (
              <span key={i} className="mono-value-seg">
                {segment}
              </span>
            ))
          : head}
      </span>
      {tail && (
        <span className="mono-value-tail" style={tailCh ? { minWidth: `min(${tailCh}ch, 100% - 6ch)` } : undefined}>
          {tail}
        </span>
      )}
    </>
  )

  return (
    <span className="mono-value" data-wrap={wrap || undefined} style={{ minWidth, fontSize: fz, color: c }}>
      <Tooltip label={whole} opened={opened} withArrow multiline maw={520} classNames={{ tooltip: 'mono-value-tip' }}>
        {href ? (
          <a
            href={href}
            target="_blank"
            rel="noreferrer"
            className="mono-value-text"
            onMouseEnter={show}
            onMouseLeave={hide}
            onFocus={showOnKeyboardFocus}
            onBlur={hide}
          >
            {text}
          </a>
        ) : (
          <span
            className="mono-value-text"
            data-cut={cut ?? undefined}
            tabIndex={hidesSome && !copy && focusable ? 0 : undefined}
            title={hidesSome && !opened ? whole : undefined}
            onMouseEnter={show}
            onMouseLeave={hide}
            onFocus={showOnKeyboardFocus}
            onBlur={hide}
          >
            {text}
          </span>
        )}
      </Tooltip>
      {copy && (
        <ActionIcon
          className="mono-value-copy"
          size="xs"
          variant="subtle"
          color={copied ? 'var(--ok)' : 'var(--neutral)'}
          aria-label={copied ? t`Copied` : t`Copy ${whole}`}
          onClick={() => void copyValue(whole)}
          onFocus={showOnKeyboardFocus}
          onBlur={hide}
        >
          {copied ? <IconCheck size={13} /> : <IconCopy size={13} />}
        </ActionIcon>
      )}
    </span>
  )
}
