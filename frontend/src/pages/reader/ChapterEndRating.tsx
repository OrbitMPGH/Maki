import { useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { Trans } from '@lingui/react/macro'
import { useDismissRateHint, useFinalizeRating, useSetRating } from '../../api/hooks'
import { RateHint } from '../../components/series/RateHint'

/**
 * The final rating, asked at the end of the series' last chapter. The same line, endpoints and "keep
 * your so-far score" shortcut as the series page's finished hint; only where it sits is new.
 */
export default function ChapterEndRating({ seriesId, soFar }: { seriesId: number; soFar: number | null }) {
  const queryClient = useQueryClient()
  const setRating = useSetRating()
  const finalize = useFinalizeRating()
  const dismiss = useDismissRateHint()
  const [answer, setAnswer] = useState<{ rating: number | null } | null>(null)

  const settle = (rating: number | null) => {
    setAnswer({ rating })
    void queryClient.invalidateQueries({ queryKey: ['reader-ending', seriesId] })
  }

  if (answer) {
    const rating = answer.rating
    return rating == null ? null : (
      <span className="reader-end-note" role="status">
        <Trans>Rated {rating}/10</Trans>
      </span>
    )
  }

  return (
    <RateHint
      soFar={soFar}
      onRate={(rating) => setRating.mutate({ seriesId, rating }, { onSuccess: () => settle(rating) })}
      onKeep={() => finalize.mutate({ seriesId }, { onSuccess: ({ rating }) => settle(rating ?? soFar) })}
      onDismiss={() => dismiss.mutate(seriesId, { onSuccess: () => settle(null) })}
      wrap="wrap"
    />
  )
}
