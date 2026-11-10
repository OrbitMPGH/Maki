import { Anchor, Badge, Group, Skeleton, Spoiler, Stack, Text } from '@mantine/core'
import { IconExternalLink, IconMessage2, IconStarFilled } from '../../lib/icons'
import { Trans, useLingui } from '@lingui/react/macro'
import { useMangaReviews, type MangaReview } from '../../api/hooks'
import { Panel } from '../ui/Panel'
import { PanelTitle } from '../ui/PanelTitle'

/**
 * What readers made of a series, scraped from MyAnimeList when the card opens.
 *
 * Heads the column beside the synopsis, so the reviews are in view when the modal opens however
 * long the synopsis and tags run. Renders nothing at all when there is no MAL id to ask about.
 */
export function DiscoverReviews({ malId }: { malId: number | null }) {
  const { data: reviews, isLoading } = useMangaReviews(malId)

  if (malId == null) return null
  // `undefined` is nothing back yet, `null` is the upstream fetch failing, `[]` is a series nobody
  // has reviewed. Only the failure is worth a panel; an empty bordered box at the foot of the
  // column reads as something that broke.
  if (!isLoading && reviews !== null && (reviews?.length ?? 0) === 0) return null

  return (
    <Panel>
      <Group justify="space-between" align="baseline" wrap="nowrap">
        <PanelTitle order={2} icon={IconMessage2}>
          <Trans>Reviews</Trans>
        </PanelTitle>
        <Text size="xs" c="var(--ink-4)">
          <Trans>From MyAnimeList</Trans>
        </Text>
      </Group>

      <ReviewList reviews={reviews} isLoading={isLoading} />
    </Panel>
  )
}

/** The placeholder, the upstream-failure line and the reviews, shared with the series page drawer. */
export function ReviewList({
  reviews,
  isLoading,
}: {
  reviews: MangaReview[] | null | undefined
  isLoading: boolean
}) {
  const { t } = useLingui()
  const reviewLabel = (author: string) => t`Review by ${author}`

  return (
    <>
      {isLoading && (
        <Stack gap="xs" mt="md" aria-hidden>
          <Skeleton h={14} w="40%" />
          <Skeleton h={12} />
          <Skeleton h={12} />
          <Skeleton h={12} w="70%" />
        </Stack>
      )}

      {/* Null is the upstream fetch failing, which is worth saying out loud: an empty list would
          otherwise read as "nobody has reviewed this". */}
      {!isLoading && reviews === null && (
        <Text size="sm" c="var(--ink-4)" mt="sm">
          <Trans>MyAnimeList didn't respond, so there are no reviews to show right now.</Trans>
        </Text>
      )}

      {reviews && reviews.length > 0 && (
        <div className="review-list">
          {reviews.map((review, i) => (
            <article key={i} className="review-row">
              <Group justify="space-between" wrap="nowrap" mb={6} gap="xs">
                <Group gap="xs" wrap="wrap" style={{ minWidth: 0 }}>
                  {review.score != null && (
                    <span className="review-score tnum">
                      <IconStarFilled size={12} color="var(--rating)" aria-hidden />
                      {review.score}
                    </span>
                  )}
                  <Text size="sm" fw={600} c="var(--ink-hi)">
                    {review.author}
                  </Text>
                  {review.tags.map((t) => (
                    <Badge key={t} size="xs" variant="light" color="var(--neutral)">
                      {t}
                    </Badge>
                  ))}
                </Group>
                {review.url && (
                  <Anchor
                    href={review.url}
                    target="_blank"
                    rel="noopener noreferrer"
                    size="xs"
                    c="var(--ink-3)"
                    style={{ whiteSpace: 'nowrap' }}
                  >
                    <Group gap={3} wrap="nowrap">
                      <Trans>Full review</Trans> <IconExternalLink size={12} />
                    </Group>
                  </Anchor>
                )}
              </Group>
              <Spoiler
                maxHeight={72}
                showLabel={t`Show more`}
                hideLabel={t`Show less`}
                attributes={{ content: { 'aria-label': reviewLabel(review.author) } }}
              >
                <Text size="sm" c="var(--ink-3)" style={{ whiteSpace: 'pre-line', overflowWrap: 'anywhere', lineHeight: 1.6 }}>
                  {review.text}
                </Text>
              </Spoiler>
            </article>
          ))}
        </div>
      )}
    </>
  )
}
