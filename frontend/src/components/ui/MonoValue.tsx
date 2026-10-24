import { useLayoutEffect, useRef, useState, type CSSProperties, type FocusEvent } from 'react'
import { ActionIcon, Tooltip } from '@mantine/core'
import { IconCheck, IconCopy } from '@tabler/icons-react'
import { useLingui } from '@lingui/react/macro'
import { useCopyText } from './useCopyText'

const NUMBER_TOKEN = /(?<=^|[\s_\-[(./\\])(?:vol(?:ume)?\.?|ch(?:apter)?\.?|v|c)\s?\d/gi
const MAX_TAIL = 32

/**
 * What stays when the middle goes. Release names put the volume or chapter number in the middle
 * and tags at the end, so the tail starts at the first number token that leaves at most 32
 * characters ("v01 (2022) (Digital).cbz"). Without one it is the extension and eight characters
 * before it, or the last six characters of an id or path.
 */
export function splitForTruncation(value: string): [head: string, tail: string] {
  for (const m of value.matchAll(NUMBER_TOKEN)) {
    if (m.index > 0 && value.length - m.index <= MAX_TAIL) return [value.slice(0, m.index), value.slice(m.index)]
  }
  const ext = /\.[A-Za-z0-9]{1,5}$/.exec(value)?.[0] ?? ''
  const keep = ext ? ext.length + 8 : 6
  if (value.length <= Math.max(keep + 4, 16)) return [value, '']
  return [value.slice(0, -keep), value.slice(-keep)]
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
 * on focus; without it the clipped value itself is focusable. It never wraps, so a table holding one needs a min width and scrolls
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
  fz?: CSSProperties['fontSize']
  c?: CSSProperties['color']
}) {
  const { t } = useLingui()
  const { copied, copy: copyValue } = useCopyText()
  const headRef = useRef<HTMLSpanElement>(null)
  const [opened, setOpened] = useState(false)
  const [clipped, setClipped] = useState(false)
  const whole = full ?? value
  const [head, tail] = splitForTruncation(value)
  // Clipped, or standing for something longer: only then is there anything to reveal. The value
  // takes focus for that only when it has no copy button, which reveals the same tooltip on focus,
  // so a table of values has one tab stop per row. The full text is in the DOM either way.
  const hidesSome = whole !== value || clipped

  useLayoutEffect(() => {
    const el = headRef.current
    const box = el?.parentElement
    if (!el || !box) return
    const over = (e: Element | null) => !!e && e.scrollWidth > e.clientWidth
    return watchSize(box, () => setClipped(over(el) || over(el.nextElementSibling)))
  }, [value])

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
        {head}
      </span>
      {tail && <span className="mono-value-tail">{tail}</span>}
    </>
  )

  return (
    <span className="mono-value" style={{ minWidth, fontSize: fz, color: c }}>
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
