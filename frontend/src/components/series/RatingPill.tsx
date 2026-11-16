import type { ComponentPropsWithRef } from 'react'
import { UnstyledButton } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { useRatingFormat } from '../../api/ratings'
import { RATING_WORDS, ratingWordFor, useRatingWord } from '../../lib/ratingWords'

/**
 * The reader's score as its word and number ("Very good 7.8") over a thin bar of seven, the button
 * that opens the rating panel. A "so far" score is grey; with no score it reads Rate.
 */
export function RatingPill({
  value,
  soFar = false,
  opened = false,
  size = 'md',
  ...rest
}: {
  value: number | null
  soFar?: boolean
  opened?: boolean
  size?: 'sm' | 'md'
} & Omit<ComponentPropsWithRef<'button'>, 'value' | 'children'>) {
  const { t } = useLingui()
  const { format } = useRatingFormat()
  const word = useRatingWord()
  const state = value == null ? 'unrated' : soFar ? 'so-far' : 'rated'
  const shown = value != null ? `${word(value)} ${format(value)}` : null
  const level = value != null ? RATING_WORDS.length - 1 - RATING_WORDS.indexOf(ratingWordFor(value)) : -1

  return (
    <UnstyledButton
      {...rest}
      className="rating-pill"
      data-state={state}
      data-size={size}
      data-open={opened || undefined}
      aria-expanded={opened}
      aria-label={shown != null ? (soFar ? t`Your rating: ${shown} so far` : t`Your rating: ${shown}`) : t`Rate this series`}
    >
      <span className="rating-pill-text">
        {value != null ? (
          <>
            <span>{word(value)}</span>
            <span className="rating-pill-score tnum">{format(value)}</span>
            {soFar && (
              <span className="rating-pill-so-far">
                <Trans>so far</Trans>
              </span>
            )}
          </>
        ) : (
          <Trans>Rate</Trans>
        )}
      </span>
      <span className="rating-pill-bar" aria-hidden>
        {RATING_WORDS.map((w, i) => (
          <span key={w.score} data-filled={i <= level || undefined} />
        ))}
      </span>
    </UnstyledButton>
  )
}
