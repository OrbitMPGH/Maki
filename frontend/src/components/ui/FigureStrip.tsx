import { useId, useState, type ReactNode } from 'react'
import { Skeleton, Tooltip, VisuallyHidden } from '@mantine/core'
import { formatNumber, formatSignedPercent } from '../../format'
import { IconInfoCircle } from '../../lib/icons'

export interface Figure {
  label: string
  /** A count, formatted here, or a value the caller already formatted (a duration, a percentage, "3 / 1"). */
  value: number | ReactNode
  /** Only coloured while non-zero: an empty "Failed" is good news and should not look like a warning. */
  tone?: 'ok' | 'warn' | 'danger' | 'info'
  /** Whether a formatted value stands for nothing, so it greys out like a zero count does. */
  zero?: boolean
  /** Change against the previous period as a fraction (0.12 prints +12%), as a chip beside the value. */
  delta?: number
  /** A dimmed line under the label, for a related count the figure does not headline. */
  hint?: ReactNode
  /** What the label means, in plain words, as a tooltip on the label. */
  tip?: string
  /** A small graphic beside the value and label, such as the mascot. */
  mark?: ReactNode
}

/**
 * A row of labelled counts in one ruled strip, the operational counterpart of the hero figures.
 * `flush` drops the strip's own border and surface for when it already sits inside a panel.
 * `loading` keeps the labels and blanks the numbers, so a count never reads as zero before it arrives.
 * `deltaLabel` names what the deltas compare against; it is the chips' tooltip.
 */
export function FigureStrip({
  figures,
  flush,
  loading,
  deltaLabel,
  className,
}: {
  figures: Figure[]
  flush?: boolean
  loading?: boolean
  deltaLabel?: string
  className?: string
}) {
  const isZero = (f: Figure) => f.zero ?? f.value === 0
  return (
    <div
      className={className ? `figure-strip ${className}` : 'figure-strip'}
      data-flush={flush || undefined}
    >
      {figures.map((f) => {
        const zero = isZero(f)
        const n = (
          <span
            className="figure-strip-n tnum"
            style={f.tone && !zero ? { color: `var(--${f.tone})` } : undefined}
          >
            {typeof f.value === 'number' ? formatNumber(f.value) : f.value}
          </span>
        )
        return (
          <div
            className="figure-strip-item"
            key={f.label}
            data-zero={(!loading && zero) || undefined}
            data-mark={f.mark ? true : undefined}
          >
            {f.mark && <span className="figure-strip-mark">{f.mark}</span>}
            {loading ? (
              <div className="figure-strip-n">
                <Skeleton h="0.8em" w="2.4em" my="0.125em" />
              </div>
            ) : f.delta !== undefined && Math.round(f.delta * 100) !== 0 && !zero ? (
              // "-100%" beside a 0 would repeat the 0 as an alarm, and "0%" beside "5/30" reads as a share.
              <div className="figure-strip-value">
                {n}
                <span
                  className="figure-strip-delta tnum"
                  data-tone={f.delta > 0 ? 'up' : 'down'}
                  title={deltaLabel}
                >
                  {formatSignedPercent(f.delta)}
                </span>
              </div>
            ) : (
              n
            )}
            {f.tip ? (
              <HintLabel tip={f.tip} className="figure-strip-l">
                {f.label}
              </HintLabel>
            ) : (
              <span className="figure-strip-l">{f.label}</span>
            )}
            {f.hint && !loading && <span className="figure-strip-hint">{f.hint}</span>}
          </div>
        )
      })}
    </div>
  )
}

const HINT_EVENTS = { hover: true, focus: true, touch: true }

/**
 * A label with a plain-words tooltip, marked by an info glyph. It takes focus so the tooltip opens
 * from the keyboard, and screen readers get the tip as its description.
 */
export function HintLabel({
  tip,
  children,
  className,
  maw = 260,
}: {
  tip: string
  children: ReactNode
  className?: string
  maw?: number
}) {
  const id = useId()
  return (
    <>
      <Tooltip label={tip} withArrow multiline maw={maw} events={HINT_EVENTS}>
        <span className={className ? `column-hint ${className}` : 'column-hint'} tabIndex={0} aria-describedby={id}>
          {children}
          <IconInfoCircle className="column-hint-icon" size={13} aria-hidden="true" />
        </span>
      </Tooltip>
      <VisuallyHidden id={id}>{tip}</VisuallyHidden>
    </>
  )
}

/**
 * Shows or hides a strip with a height and opacity transition. The first render takes `shown` as is,
 * so mount it once the data has loaded. Under reduced motion the height cannot move, so a hiding
 * strip fades first and only then gives up its row.
 */
export function FigureStripReveal({ shown, children }: { shown: boolean; children: ReactNode }) {
  const [collapsed, setCollapsed] = useState(!shown)
  if (shown && collapsed) setCollapsed(false)
  return (
    <div
      className="figure-strip-reveal"
      data-hidden={!shown || undefined}
      data-collapsed={collapsed || undefined}
      inert={!shown}
      onTransitionEnd={(e) => {
        if (e.target === e.currentTarget && e.propertyName === 'opacity' && !shown) setCollapsed(true)
      }}
    >
      <div>{children}</div>
    </div>
  )
}
