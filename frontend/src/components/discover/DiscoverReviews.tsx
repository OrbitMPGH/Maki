import { Anchor, Badge, Group, Loader, Paper, Spoiler, Stack, Text, Title } from '@mantine/core'
import { IconExternalLink, IconStar } from '@tabler/icons-react'
import { useMangaReviews } from '../../api/hooks'
import { ratingBandVisual } from '../ui/status'

/**
 * What readers made of a series, scraped from MyAnimeList when the card opens.
 *
 * Sits at the foot of the reading column rather than behind a tab of its own: two or three
 * opinions are the last thing you read about a series you are deciding on, not a section worth
 * navigating to. Renders nothing at all when there is no MAL id to ask about, so the column simply
 * ends after the tags.
 */
export function DiscoverReviews({ malId }: { malId: number | null }) {
  const { data: reviews, isLoading } = useMangaReviews(malId)

  if (malId == null) return null
  // `undefined` is nothing back yet, `null` is the upstream fetch failing, `[]` is a series nobody
  // has reviewed. Only the failure is worth a panel; an empty bordered box at the foot of the
  // column reads as something that broke.
  if (!isLoading && reviews !== null && (reviews?.length ?? 0) === 0) return null

  return (
    <Paper withBorder radius="lg" p="lg">
      <Group justify="space-between" align="baseline">
        <Title order={3} fz={17}>
          Reviews
        </Title>
        <Text size="xs" c="var(--ink-4)">
          From MyAnimeList
        </Text>
      </Group>

      {isLoading && (
        <Group justify="center" py="lg">
          <Loader size="sm" />
        </Group>
      )}

      {/* Null is the upstream fetch failing, which is worth saying out loud: an empty list would
          otherwise read as "nobody has reviewed this". */}
      {!isLoading && reviews === null && (
        <Text size="sm" c="var(--ink-4)" mt="sm">
          MyAnimeList didn't respond, so there are no reviews to show right now.
        </Text>
      )}

      <Stack gap="sm" mt="md">
        {reviews?.map((review, i) => (
          <Paper key={i} withBorder radius="md" p="sm" bg="var(--surface-2)">
            <Group justify="space-between" wrap="nowrap" mb={6}>
              <Group gap="xs">
                {review.score != null && (
                  <Badge
                    size="sm"
                    color={ratingBandVisual(review.score * 10).color}
                    leftSection={<IconStar size={11} />}
                  >
                    {review.score}
                  </Badge>
                )}
                <Text size="sm" fw={600}>
                  {review.author}
                </Text>
                {review.tags.map((t) => (
                  <Badge key={t} size="xs" variant="light" color="gray">
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
                  style={{ whiteSpace: 'nowrap' }}
                >
                  <Group gap={3}>
                    Full review <IconExternalLink size={12} />
                  </Group>
                </Anchor>
              )}
            </Group>
            <Spoiler maxHeight={72} showLabel="Show more" hideLabel="Show less">
              <Text size="sm" c="var(--ink-3)" style={{ whiteSpace: 'pre-line', lineHeight: 1.6 }}>
                {review.text}
              </Text>
            </Spoiler>
          </Paper>
        ))}
      </Stack>
    </Paper>
  )
}
