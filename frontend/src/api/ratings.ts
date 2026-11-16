import { useCallback } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { useLingui } from '@lingui/react/macro'
import { api } from './client'
import { optimisticMerge } from './optimistic'
import type { SeriesDto } from './types'
import { formatRating, type RatingCategory, type RatingPrecision } from '../lib/rating'

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
    mutationFn: ({ seriesId, scores }: { seriesId: number; scores: Record<string, number> }) =>
      api<{ ratingBreakdown: Record<string, number> | null }>(`/series/${seriesId}/rating/breakdown`, {
        method: 'PUT',
        body: JSON.stringify({ scores }),
      }),
    onSuccess: ({ ratingBreakdown }, { seriesId }) => {
      queryClient.setQueryData<SeriesDto>(['series', seriesId], (old) => (old ? { ...old, ratingBreakdown } : old))
    },
  })
}
