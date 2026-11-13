import type { HTMLAttributes } from 'react'
import { formatFixedDecimal } from '../../format'
import { IconStarFilled } from '../../lib/icons'

type Size = 'sm' | 'md'
type Tone = 'community' | 'yours'
type SpanProps = Omit<HTMLAttributes<HTMLSpanElement>, 'children'>

/** The shared markup: a star and a number, inline and off cover art. */
export function ScoreInlineMark({
  text,
  size = 'sm',
  tone = 'community',
  className,
  ...rest
}: { text: string; size?: Size; tone?: Tone } & SpanProps) {
  return (
    <span className={className ? `score-inline ${className}` : 'score-inline'} data-tone={tone} {...rest}>
      <IconStarFilled size={size === 'md' ? 14 : 12} aria-hidden />
      {text}
    </span>
  )
}

/** A community score on the API's 0-100 scale, shown out of ten with one decimal. */
export function ScoreInline({
  score,
  size,
  tone,
  className,
  ...rest
}: { score: number; size?: Size; tone?: Tone; className?: string } & SpanProps) {
  return (
    <ScoreInlineMark text={formatFixedDecimal(score / 10, 1)} size={size} tone={tone} className={className} {...rest} />
  )
}

/** The user's own 1-10 rating: the integer, with the accent star. */
export function YourScoreInline({
  rating,
  size,
  className,
  ...rest
}: { rating: number; size?: Size; className?: string } & SpanProps) {
  return <ScoreInlineMark text={String(rating)} size={size} tone="yours" className={className} {...rest} />
}
