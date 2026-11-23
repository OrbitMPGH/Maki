import { useCallback } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { useLingui } from '@lingui/react/macro'
import { api } from './client'
import { optimisticMerge } from './optimistic'
import type { SeriesDto } from './types'
import { formatRating, type RatingCategory, type RatingPrecision } from '../lib/rating'
import { ratingWordFor } from '../lib/ratingWords'

export interface RatingSettings {
  precision: RatingPrecision
  /** Display order. Built-ins are always present; custom ones carry a name. */
  categories: RatingCategory[]
}

export const MAX_ENABLED_CATEGORIES = 8
export const MAX_CUSTOM_CATEGORIES = 8

const BUILTIN_LABELS: Record<string, MessageDescriptor> = {
  story: msg`Story`,
  characters: msg`Characters`,
  art: msg`Art`,
  enjoyment: msg`Enjoyment`,
  pacing: msg`Pacing`,
  world: msg`World`,
  originality: msg`Originality`,
  ending: msg`Ending`,
}

/** Scored only once the series is finished. */
export const ENDING_KEY = 'ending'

const DEFAULT_SETTINGS: RatingSettings = {
  precision: 'whole',
  categories: Object.keys(BUILTIN_LABELS).map((key, i) => ({ key, name: null, enabled: i < 4, weight: 1 })),
}

const settingsKey = ['settings', 'ratings']

export function useRatingSettings() {
  const query = useQuery({
    queryKey: settingsKey,
    queryFn: () => api<RatingSettings>('/settings/ratings'),
    staleTime: Infinity,
  })
  return { ...query, settings: query.data ?? DEFAULT_SETTINGS }
}

export function useSaveRatingSettings() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (settings: RatingSettings) =>
      api<RatingSettings>('/settings/ratings', { method: 'PUT', body: JSON.stringify(settings) }),
    ...optimisticMerge(queryClient, settingsKey, (settings: RatingSettings) => settings, { seededBySave: true }),
    onSuccess: (saved) => {
      queryClient.setQueryData(settingsKey, saved)
      void queryClient.invalidateQueries({ queryKey: ['series'] })
    },
  })
}

/** The reader's rating precision and a formatter for it. Whole until the setting loads. */
export function useRatingFormat() {
  const { settings } = useRatingSettings()
  const precision = settings.precision
  const format = useCallback((value: number) => formatRating(value, precision), [precision])
  return { precision, format }
}

/**
 * A score's word and number as one phrase ("Very good 7.8"), so a language can order them its way.
 * `decimal` forces one decimal, for a computed suggestion shown in whole-number mode.
 */
export function useRatingText() {
  const { t, i18n } = useLingui()
  const { precision } = useRatingSettings().settings
  return useCallback(
    (value: number, decimal = false) => {
      const word = i18n._(ratingWordFor(value).label)
      const score = formatRating(value, decimal ? 'decimal' : precision)
      return t({ message: `${word} ${score}`, comment: 'A rating word and its score, such as "Very good 7.8".' })
    },
    [t, i18n, precision],
  )
}

/** A category's name: translated for a built-in, as typed for a custom one. */
export function useCategoryLabel() {
  const { i18n } = useLingui()
  return useCallback(
    (category: Pick<RatingCategory, 'key' | 'name'>) =>
      category.name ?? (BUILTIN_LABELS[category.key] ? i18n._(BUILTIN_LABELS[category.key]) : category.key),
    [i18n],
  )
}

/** Stores the category scores. Never changes the rating itself. */
export function useSetRatingBreakdown() {
  const queryClient = useQueryClient()
  return useMutation({
    // One at a time, in order: each save replaces the whole map, so an older save landing after a
    // newer one would put the older scores back.
    scope: { id: 'rating-breakdown' },
    mutationFn: ({ seriesId, scores }: { seriesId: number; scores: Record<string, number> }) =>
      api<{ ratingBreakdown: Record<string, number> | null }>(`/series/${seriesId}/rating/breakdown`, {
        method: 'PUT',
        body: JSON.stringify({ scores }),
      }),
    // Written ahead of the request, since each save replaces the whole map: a panel reopened before
    // the answer must start from these scores, not the ones the series loaded with.
    onMutate: ({ seriesId, scores }) => {
      const ratingBreakdown = Object.keys(scores).length ? scores : null
      queryClient.setQueryData<SeriesDto>(['series', seriesId], (old) => (old ? { ...old, ratingBreakdown } : old))
    },
    onSuccess: ({ ratingBreakdown }, { seriesId }) => {
      queryClient.setQueryData<SeriesDto>(['series', seriesId], (old) => (old ? { ...old, ratingBreakdown } : old))
    },
  })
}
