import { useState } from 'react'
import { Anchor, Button, Drawer, Group, Stack, Text, Title } from '@mantine/core'
import { IconExternalLink, IconMessage2 } from '@tabler/icons-react'
import { Trans, useLingui } from '@lingui/react/macro'
import { useMangaReviews } from '../../api/hooks'
import { ReviewList } from '../discover/DiscoverReviews'

/**
 * A Reviews button beside the series page's metadata links, opening MyAnimeList reviews in a
 * drawer. Unlike the Discover modal nothing is fetched until the button is pressed: every review
 * load is a MAL page scrape, and a series already in the library rarely needs them.
 */
export function SeriesReviews({ malId }: { malId: number }) {
  const { t } = useLingui()
  const [opened, setOpened] = useState(false)
  // Stays set after the drawer closes, so the list doesn't blank out under the close transition.
  const [requested, setRequested] = useState(false)
  const { data: reviews, isLoading } = useMangaReviews(requested ? malId : null)

  return (
    <>
      <Button
        size="xs"
        variant="light"
        leftSection={<IconMessage2 size={14} />}
        onClick={() => {
          setRequested(true)
          setOpened(true)
        }}
      >
        <Trans>Reviews</Trans>
      </Button>

      <Drawer
        opened={opened}
        onClose={() => setOpened(false)}
        position="right"
        size="md"
        title={
          <Group gap="xs" wrap="nowrap">
            <IconMessage2 size={20} color="var(--brand-fg)" />
            <Stack gap={0}>
              <Title order={3} fz={17}>
                <Trans>Reviews</Trans>
              </Title>
              <Text size="xs" c="var(--ink-4)">
                <Trans>From MyAnimeList</Trans>
              </Text>
            </Stack>
          </Group>
        }
        closeButtonProps={{ 'aria-label': t`Close` }}
      >
        <Stack gap="md" pb="xl">
          <div>
            <ReviewList reviews={reviews} isLoading={isLoading} />
            {!isLoading && reviews?.length === 0 && (
              <Text size="sm" c="var(--ink-4)">
                <Trans>No reviews on MyAnimeList yet.</Trans>
              </Text>
            )}
          </div>
          <Anchor
            href={`https://myanimelist.net/manga/${malId}/_/reviews`}
            target="_blank"
            rel="noopener noreferrer"
            size="sm"
          >
            <Group gap={4}>
              <Trans>Read more on MyAnimeList</Trans> <IconExternalLink size={14} />
            </Group>
          </Anchor>
        </Stack>
      </Drawer>
    </>
  )
}
