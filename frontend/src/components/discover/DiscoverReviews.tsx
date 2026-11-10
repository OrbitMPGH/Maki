import { Anchor, Badge, Group, Skeleton, Spoiler, Stack, Text, ThemeIcon, Tooltip } from '@mantine/core'
import { IconExternalLink, IconMessage2, IconStarFilled, IconThumbUp } from '../../lib/icons'
import { Trans, useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'
import {
  hasReviewIds,
  useSeriesReviews,
  type MangaReview,
  type ReviewIds,
  type SeriesReviews,
} from '../../api/hooks'
import { formatDecimal, formatNumber } from '../../format'
import { siteInfo } from '../../lib/metadataSites'
import { MetadataSiteIcon } from '../MetadataSiteIcon'
import { Panel } from '../ui/Panel'
import { PanelTitle } from '../ui/PanelTitle'

// The detail modal sits at 1000; its tooltips have to clear it.
const TOOLTIP_Z = 1001

/**
 * What readers made of a series: full reviews from MyAnimeList and AniList, then a couple of
 * Kitsu's one-line takes, fetched when the card opens. Each carries its site's logo.
 *
 * Heads the column beside the synopsis, so the reviews are in view when the modal opens however
 * long the synopsis and tags run. Renders nothing when there is no id to ask about, or when every
 * site answered and nobody has reviewed it.
 */
export function DiscoverReviews({ ids }: { ids: ReviewIds }) {
  const { data, isLoading, isError } = useSeriesReviews(ids)

  if (!hasReviewIds(ids)) return null
  // An empty bordered box at the foot of the column reads as something that broke, so only a
  // failure is worth a panel when there is nothing to show.
  if (data && data.reviews.length === 0 && data.quickTakes.length === 0 && data.unavailable.length === 0) return null

  return (
    <Panel>
      <PanelTitle order={2} icon={IconMessage2}>
        <Trans>Reviews</Trans>
      </PanelTitle>

      <ReviewList data={data} isLoading={isLoading} isError={isError} />
    </Panel>
  )
}

/** The placeholder, the failure lines, the reviews and the quick takes, shared with the series page drawer. */
export function ReviewList({
  data,
  isLoading,
  isError = false,
}: {
  data: SeriesReviews | undefined
  isLoading: boolean
  isError?: boolean
}) {
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

      {isError && (
        <Text size="sm" c="var(--ink-4)" mt="sm">
          <Trans>Reviews couldn't be loaded right now.</Trans>
        </Text>
      )}

      {data && data.reviews.length > 0 && (
        <div className="review-list">
          {data.reviews.map((review, i) => (
            <ReviewRow key={i} review={review} />
          ))}
        </div>
      )}

      {data && data.quickTakes.length > 0 && (
        <div className="review-quick-takes">
          <Text size="xs" fw={600} c="var(--ink-3)" mb={4}>
            <Trans>Quick takes</Trans>
          </Text>
          <div className="review-list">
            {data.quickTakes.map((take, i) => (
              <article key={i} className="review-row">
                <ReviewByline review={take} />
                <Text size="sm" c="var(--ink-3)" style={{ overflowWrap: 'anywhere', lineHeight: 1.6 }}>
                  {take.text}
                </Text>
              </article>
            ))}
          </div>
        </div>
      )}

      {/* A site that didn't answer is worth saying out loud: a missing site would otherwise read as
          "nobody reviewed it there". */}
      {data?.unavailable.map((site) => (
        <UnavailableNote key={site} site={site} />
      ))}
    </>
  )
}

function ReviewRow({ review }: { review: MangaReview }) {
  const { t } = useLingui()
  const author = review.author

  return (
    <article className="review-row">
      <ReviewByline review={review} />
      {review.title && (
        <Text size="sm" fw={600} c="var(--ink-hi)" mb={4} style={{ overflowWrap: 'anywhere' }}>
          {review.title}
        </Text>
      )}
      <Spoiler
        maxHeight={72}
        showLabel={t`Show more`}
        hideLabel={t`Show less`}
        attributes={{ content: { 'aria-label': t`Review by ${author}` } }}
      >
        <Text size="sm" c="var(--ink-3)" style={{ whiteSpace: 'pre-line', overflowWrap: 'anywhere', lineHeight: 1.6 }}>
          {review.text}
        </Text>
      </Spoiler>
    </article>
  )
}

/** Site logo, score, author, sentiment tags and upvotes, with the link to the full review on the right. */
function ReviewByline({ review }: { review: MangaReview }) {
  const likes = review.likes ?? 0
  const helpful = plural(likes, {
    one: '# reader found this helpful',
    other: '# readers found this helpful',
  })

  return (
    <Group justify="space-between" wrap="nowrap" mb={6} gap="xs">
      <Group gap="xs" wrap="wrap" style={{ minWidth: 0 }}>
        <SourceMark site={review.source} />
        {review.score != null && (
          <span className="review-score tnum">
            <IconStarFilled size={12} color="var(--rating)" aria-hidden />
            {formatDecimal(review.score)}
          </span>
        )}
        <Text size="sm" fw={600} c="var(--ink-hi)">
          {review.author}
        </Text>
        {review.tags.map((tag) => (
          <Badge key={tag} size="xs" variant="light" color="var(--neutral)">
            {tag}
          </Badge>
        ))}
        {likes > 0 && (
          <Tooltip label={helpful} withArrow zIndex={TOOLTIP_Z}>
            <span className="review-likes tnum" role="img" aria-label={helpful}>
              <IconThumbUp size={12} aria-hidden />
              {formatNumber(likes)}
            </span>
          </Tooltip>
        )}
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
  )
}

/** The review's site as its logo, named by the tooltip and to screen readers. */
function SourceMark({ site }: { site: string }) {
  const { t } = useLingui()
  const { label, short, color } = siteInfo(site)

  return (
    <Tooltip label={t`From ${label}`} withArrow zIndex={TOOLTIP_Z}>
      <ThemeIcon size={22} radius="sm" variant="light" color={color} role="img" aria-label={t`From ${label}`}>
        <MetadataSiteIcon site={site} monogram={short} size={13} />
      </ThemeIcon>
    </Tooltip>
  )
}

function UnavailableNote({ site }: { site: string }) {
  const { label } = siteInfo(site)

  return (
    <Text size="sm" c="var(--ink-4)" mt="sm">
      <Trans>{label} didn't respond, so its reviews are missing for now.</Trans>
    </Text>
  )
}
