import { useCallback } from 'react'
import { msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { useLingui } from '@lingui/react/macro'

export interface RatingWord {
  /** The score picking the word sets. */
  score: number
  /** The lowest score the word names. */
  from: number
  label: MessageDescriptor
}

/** Best first. Each word sets a whole score, so every tracker stores it exactly. */
export const RATING_WORDS: readonly RatingWord[] = [
  { score: 10, from: 9.5, label: msg({ message: 'Masterpiece', context: 'rating' }) },
  { score: 9, from: 8.5, label: msg({ message: 'Excellent', context: 'rating' }) },
  { score: 8, from: 7.5, label: msg({ message: 'Very good', context: 'rating' }) },
  { score: 7, from: 6.5, label: msg({ message: 'Good', context: 'rating' }) },
  { score: 6, from: 5.5, label: msg({ message: 'Decent', context: 'rating' }) },
  { score: 5, from: 4.5, label: msg({ message: 'Average', context: 'rating' }) },
  { score: 3, from: 1, label: msg({ message: 'Bad', context: 'rating' }) },
]

/** The word a score falls under: 7.8 is Very good, 4 is Bad. */
export function ratingWordFor(score: number): RatingWord {
  return RATING_WORDS.find((w) => score >= w.from) ?? RATING_WORDS[RATING_WORDS.length - 1]
}

/** The translated word for a score. */
export function useRatingWord() {
  const { i18n } = useLingui()
  return useCallback((score: number) => i18n._(ratingWordFor(score).label), [i18n])
}
