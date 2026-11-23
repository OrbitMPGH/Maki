import { useEffect, useLayoutEffect, useRef, useState, type ComponentPropsWithRef, type CSSProperties, type KeyboardEvent, type MouseEvent, type PointerEvent, type Ref } from 'react'
import { UnstyledButton } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { useRatingFormat, useRatingText } from '../../api/ratings'
import { useRatingWord } from '../../lib/ratingWords'

const SCORES = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]
/** Resting on the dashes this long opens the ten buttons; the dashes start growing at once. */
const OPEN_DELAY_MS = 250
/** A slip of the pointer off the buttons this short keeps them open. */
const CLOSE_GRACE_MS = 300
/** After a pick, the buttons show the result this long before they fold away. */
const SETTLE_MS = 650
/** Matches the fold-away animation in theme.css. */
const CLOSE_MS = 180
/** The dashes travelling into their buttons, all together, so every one lands at the same moment. */
const MORPH_MS = 280
/**
 * The dashes while the pointer waits on them (`--grow` in theme.css): a quick first grow, so even a
 * pointer passing over shows they react, then a slow creep, then a rush into the buttons.
 */
const EAGER_FRAMES: Keyframe[] = [
  { '--grow': 0, easing: 'cubic-bezier(0.2, 0.9, 0.3, 1)' },
  { '--grow': 0.38, offset: 0.22, easing: 'linear' },
  { '--grow': 0.7, offset: 0.8, easing: 'cubic-bezier(0.5, 0, 1, 0.8)' },
  { '--grow': 1 },
]
const reducedMotion = () => window.matchMedia('(prefers-reduced-motion: reduce)').matches

/** How much of score step `n` a value fills, 0 to 1: 6.5 fills 1 to 6 and half of 7. */
const fillOf = (value: number | null, n: number) => (value == null ? 0 : Math.max(0, Math.min(1, value - (n - 1))))

type Quick = 'idle' | 'eager' | 'open' | 'closing'

/**
 * The reader's score as its word and number ("Very good 7.8") over a bar of ten dashes, one per
 * score. A "so far" score is grey; with no score it reads Rate.
 *
 * Without `onRate` the whole pill is one button that opens the rating panel. With it, the word opens
 * the panel and the dashes rate: resting on them grows them into ten numbered buttons on a layer over
 * the row, and a click on the dashes opens that at once. A touch screen has no hover, so a tap
 * anywhere opens the panel instead.
 */
export function RatingPill({
  value,
  soFar = false,
  opened = false,
  size = 'md',
  onRate,
  ref,
  ...rest
}: {
  value: number | null
  soFar?: boolean
  opened?: boolean
  size?: 'sm' | 'md'
  /** Rates with one of the ten buttons. */
  onRate?: (score: number) => void
} & Omit<ComponentPropsWithRef<'button'>, 'value' | 'children'>) {
  const { t } = useLingui()
  const ratingText = useRatingText()
  const shown = value != null ? ratingText(value) : null
  const state = value == null ? 'unrated' : soFar ? 'so-far' : 'rated'
  const label = shown != null ? (soFar ? t`Your rating: ${shown} so far` : t`Your rating: ${shown}`) : t`Rate this series`

  if (!onRate) {
    return (
      <UnstyledButton
        {...rest}
        ref={ref}
        className="rating-pill"
        data-state={state}
        data-size={size}
        data-open={opened || undefined}
        aria-expanded={opened}
        aria-label={label}
      >
        <PillText value={value} soFar={soFar} />
        <Dashes value={value} />
      </UnstyledButton>
    )
  }
  return (
    <QuickRatingPill
      {...rest}
      ref={ref as Ref<HTMLButtonElement>}
      value={value}
      soFar={soFar}
      opened={opened}
      size={size}
      state={state}
      label={label}
      onRate={onRate}
    />
  )
}

function PillText({ value, soFar, preview }: { value: number | null; soFar: boolean; preview?: number | null }) {
  const { format } = useRatingFormat()
  const word = useRatingWord()
  const v = preview ?? value
  return (
    <span className="rating-pill-text">
      {v != null ? (
        <>
          <span>{word(v)}</span>
          <span className="rating-pill-score tnum">{format(v)}</span>
          {soFar && preview == null && (
            <span className="rating-pill-so-far">
              <Trans>so far</Trans>
            </span>
          )}
        </>
      ) : (
        <Trans>Rate</Trans>
      )}
    </span>
  )
}

function Dashes({ value }: { value: number | null }) {
  return (
    <span className="rating-pill-bar" aria-hidden>
      {SCORES.map((n) => (
        <span key={n} style={{ '--i': n - 1 } as CSSProperties}>
          <i style={{ width: `${fillOf(value, n) * 100}%` }} />
        </span>
      ))}
    </span>
  )
}

function QuickRatingPill({
  value,
  soFar,
  opened,
  size,
  state,
  label,
  onRate,
  onClick,
  disabled,
  ref,
  ...rest
}: {
  value: number | null
  soFar: boolean
  opened: boolean
  size: 'sm' | 'md'
  state: string
  label: string
  onRate: (score: number) => void
} & Omit<ComponentPropsWithRef<'button'>, 'value' | 'children'>) {
  const { t } = useLingui()
  const ratingText = useRatingText()
  const [quick, setQuick] = useState<Quick>('idle')
  const [preview, setPreview] = useState<number | null>(null)
  const [picked, setPicked] = useState<number | null>(null)
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null)
  const pointerType = useRef('mouse')
  // After a pick the buttons stay shut until the pointer leaves, so a resting mouse does not reopen them.
  const locked = useRef(false)
  const root = useRef<HTMLDivElement | null>(null)
  const dashes = useRef<HTMLButtonElement | null>(null)
  const buttons = useRef<(HTMLButtonElement | null)[]>([])

  const growth = useRef<Animation | null>(null)
  const bar = () => dashes.current?.querySelector<HTMLElement>('.rating-pill-bar') ?? null
  const grow = () => {
    const el = bar()
    if (!el || reducedMotion()) return
    growth.current?.cancel()
    growth.current = el.animate(EAGER_FRAMES, { duration: OPEN_DELAY_MS, fill: 'forwards' })
  }
  /** Back to resting dashes from wherever the growth got to. */
  const settle = () => {
    const el = bar()
    if (!el) return
    const from = parseFloat(getComputedStyle(el).getPropertyValue('--grow')) || 0
    growth.current?.cancel()
    growth.current = null
    if (from > 0 && !reducedMotion()) {
      el.animate([{ '--grow': from }, { '--grow': 0 }], { duration: 180, easing: 'cubic-bezier(0.2, 0.8, 0.3, 1)' })
    }
  }

  const dashRects = () =>
    [...(dashes.current?.querySelectorAll<HTMLElement>('.rating-pill-bar > span') ?? [])].map((d) => d.getBoundingClientRect())
  const onto = (from: DOMRect, button: HTMLElement) => {
    const to = button.getBoundingClientRect()
    return `translate(${from.left - to.left}px, ${from.top - to.top}px) scale(${from.width / to.width}, ${from.height / to.height})`
  }
  const morphFrom = useRef<DOMRect[] | null>(null)

  const clear = () => {
    if (timer.current) clearTimeout(timer.current)
    timer.current = null
  }
  const after = (ms: number, fn: () => void) => {
    clear()
    timer.current = setTimeout(fn, ms)
  }
  useEffect(() => () => {
    clear()
    growth.current?.cancel()
  }, [])
  // The saved value catches up with the pick once the request returns.
  useEffect(() => setPicked(null), [value])
  useLayoutEffect(() => {
    const from = morphFrom.current
    if (quick !== 'open' || !from) return
    morphFrom.current = null
    buttons.current.forEach((b, i) => {
      if (!b || !from[i]) return
      const color = getComputedStyle(b).color
      b.animate(
        [
          { transform: onto(from[i], b), color: 'transparent' },
          { color: 'transparent', offset: 0.5 },
          { transform: 'none', color },
        ],
        { duration: MORPH_MS, easing: 'cubic-bezier(0.22, 1, 0.36, 1)', fill: 'backwards' },
      )
    })
  }, [quick])

  const open = () => {
    clear()
    if (root.current) {
      const box = root.current.getBoundingClientRect()
      root.current.style.setProperty('--pill-w', `${box.width}px`)
      root.current.style.setProperty('--pill-h', `${box.height}px`)
    }
    morphFrom.current = reducedMotion() ? null : dashRects()
    setQuick('open')
  }
  const close = (refocus = false) => {
    setPreview(null)
    // Each button shrinks back into its dash, then the dashes settle.
    const rects = reducedMotion() ? [] : dashRects()
    buttons.current.forEach((b, i) => {
      if (!b || !rects[i]) return
      b.animate([{ transform: 'none' }, { transform: onto(rects[i], b), color: 'transparent' }], {
        duration: CLOSE_MS,
        easing: 'cubic-bezier(0.4, 0, 1, 1)',
        fill: 'forwards',
      })
    })
    setQuick((q) => (q === 'open' ? 'closing' : 'idle'))
    after(CLOSE_MS, () => {
      settle()
      setQuick('idle')
    })
    if (refocus) dashes.current?.focus()
  }
  const focusScore = (n: number) => buttons.current[Math.max(1, Math.min(10, n)) - 1]?.focus()
  const current = picked ?? value
  const start = current != null ? Math.round(current) : 7

  const rate = (n: number, byKeyboard: boolean) => {
    setPicked(n)
    setPreview(null)
    onRate(n)
    locked.current = true
    if (byKeyboard) close(true)
    else after(SETTLE_MS, () => close())
  }

  const onDashesEnter = (e: PointerEvent) => {
    if (e.pointerType !== 'mouse' || disabled || locked.current || quick === 'open') return
    setQuick('eager')
    grow()
    after(OPEN_DELAY_MS, open)
  }
  const onDashesLeave = () => {
    if (quick === 'eager') {
      clear()
      settle()
      setQuick('idle')
    }
  }
  const onDashesClick = (e: MouseEvent<HTMLButtonElement>) => {
    if (pointerType.current !== 'mouse' && e.detail > 0) {
      onClick?.(e)
      return
    }
    open()
    if (e.detail === 0) requestAnimationFrame(() => focusScore(start))
  }
  const onDashesKey = (e: KeyboardEvent) => {
    if (e.key === 'ArrowDown' || e.key === 'ArrowRight' || e.key === 'ArrowUp') {
      e.preventDefault()
      open()
      requestAnimationFrame(() => focusScore(start))
    }
  }
  const onScoreKey = (e: KeyboardEvent, n: number) => {
    const to =
      e.key === 'ArrowRight' || e.key === 'ArrowUp' ? n + 1
      : e.key === 'ArrowLeft' || e.key === 'ArrowDown' ? n - 1
      : e.key === 'Home' ? 1
      : e.key === 'End' ? 10
      : /^[0-9]$/.test(e.key) ? (e.key === '0' ? 10 : Number(e.key))
      : null
    if (to != null) {
      e.preventDefault()
      focusScore(to)
    } else if (e.key === 'Escape') {
      e.preventDefault()
      e.stopPropagation()
      close(true)
    }
  }

  const layerOpen = quick === 'open' || quick === 'closing'
  const nowText = current != null ? ratingText(current) : ''

  return (
    <div
      ref={root}
      className="rating-pill rating-pill-quick"
      data-state={state}
      data-size={size}
      data-open={opened || undefined}
      data-quick={quick}
      onPointerEnter={() => {
        if (quick === 'open') clear()
      }}
      onPointerLeave={() => {
        locked.current = false
        if (quick === 'eager') {
          clear()
          settle()
          setQuick('idle')
        } else if (quick === 'open') after(CLOSE_GRACE_MS, () => close())
      }}
      onBlur={(e) => {
        if (quick === 'open' && !root.current?.contains(e.relatedTarget as Node | null)) close()
      }}
    >
      <UnstyledButton
        {...rest}
        ref={ref}
        className="rating-pill-word"
        disabled={disabled}
        aria-expanded={opened}
        aria-label={label}
        onClick={onClick}
      >
        <PillText value={current} soFar={soFar && picked == null} />
      </UnstyledButton>
      <UnstyledButton
        ref={dashes}
        className="rating-pill-dashes"
        disabled={disabled}
        aria-label={t`Rate from 1 to 10`}
        aria-expanded={layerOpen}
        onPointerDown={(e) => (pointerType.current = e.pointerType)}
        onPointerEnter={onDashesEnter}
        onPointerLeave={onDashesLeave}
        onClick={onDashesClick}
        onKeyDown={onDashesKey}
      >
        <Dashes value={current} />
      </UnstyledButton>
      {layerOpen && (
        <div className="rating-quick" data-state={quick} data-so-far={(soFar && picked == null) || undefined}>
          <div className="rating-quick-head">
            <UnstyledButton
              className="rating-pill-word"
              tabIndex={-1}
              aria-hidden
              onClick={(e) => {
                close()
                onClick?.(e)
              }}
            >
              <PillText value={current} soFar={soFar && picked == null} preview={preview} />
            </UnstyledButton>
            {preview != null && current != null && preview !== current && (
              <span className="rating-quick-now">
                <Trans>Now {nowText}</Trans>
              </span>
            )}
          </div>
          <div className="rating-quick-scores" role="group" aria-label={t`Rate from 1 to 10`} onPointerLeave={() => setPreview(null)}>
            {SCORES.map((n) => {
              const at = preview != null && n === preview
              const filled = preview != null ? (n < preview ? 1 : 0) : fillOf(current, n)
              const chosen = preview == null && current != null && Number.isInteger(current) && n === current
              return (
                <button
                  key={n}
                  ref={(el) => {
                    buttons.current[n - 1] = el
                  }}
                  type="button"
                  className="rating-quick-score tnum"
                  style={{ '--i': n - 1, '--fill': `${filled * 100}%` } as CSSProperties}
                  data-on={at || chosen || undefined}
                  data-full={filled === 1 || undefined}
                  aria-pressed={chosen}
                  aria-label={ratingText(n)}
                  // Move, not enter: the layer opens under a resting pointer, which has not picked anything yet.
                  onPointerMove={() => preview !== n && setPreview(n)}
                  onFocus={() => setPreview(n)}
                  onClick={(e) => rate(n, e.detail === 0)}
                  onKeyDown={(e) => onScoreKey(e, n)}
                >
                  {n}
                </button>
              )
            })}
          </div>
        </div>
      )}
    </div>
  )
}
