import { useRef, useState, type CSSProperties } from 'react'
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

/**
 * A machine string (file name, path, source id) on one line. When its column is too narrow it loses
 * its middle, so the start and the extension stay readable; the full value is in a tooltip and,
 * with `copy`, one click away. It never wraps, so a table holding one needs a min width and scrolls
 * as a whole. `full` is what the tooltip shows and the button copies when the label is shorter, a
 * file name standing for its path. `minWidth` stops a table column collapsing to the tail.
 */
export function MonoValue({
  value,
  full,
  href,
  copy,
  minWidth,
  fz,
  c,
}: {
  value: string
  full?: string
  href?: string
  copy?: boolean
  minWidth?: CSSProperties['minWidth']
  fz?: CSSProperties['fontSize']
  c?: CSSProperties['color']
}) {
  const { t } = useLingui()
  const { copied, copy: copyValue } = useCopyText()
  const headRef = useRef<HTMLSpanElement>(null)
  const [opened, setOpened] = useState(false)
  const whole = full ?? value
  const [head, tail] = splitForTruncation(value)

  const show = () => {
    const el = headRef.current
    const tailEl = el?.nextElementSibling
    const clipped = (e: Element | null | undefined) => !!e && e.scrollWidth > e.clientWidth
    setOpened(whole !== value || clipped(el) || clipped(tailEl))
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
            onFocus={show}
            onBlur={hide}
          >
            {text}
          </a>
        ) : (
          <span className="mono-value-text" onMouseEnter={show} onMouseLeave={hide}>
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
        >
          {copied ? <IconCheck size={13} /> : <IconCopy size={13} />}
        </ActionIcon>
      )}
    </span>
  )
}
