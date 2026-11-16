import { useState, type ReactNode } from 'react'
import { Anchor, Button, Drawer, Group, Stack, Text, Title } from '@mantine/core'
import { IconExternalLink, IconMessage2 } from '../../lib/icons'
import { Trans, useLingui } from '@lingui/react/macro'
import { useSeriesReviews, type ReviewIds } from '../../api/hooks'
import { ReviewList } from '../discover/DiscoverReviews'

/**
 * A Reviews button beside the series page's metadata links, opening MyAnimeList, AniList and Kitsu
 * reviews in a drawer. Unlike the Discover modal nothing is fetched until the button is pressed:
 * every load is three outside requests, and a series already in the library rarely needs them.
 */
export function SeriesReviews({ ids }: { ids: ReviewIds }) {
  const { t } = useLingui()
  const [opened, setOpened] = useState(false)
  // Stays set after the drawer closes, so the list doesn't blank out under the close transition.
  const [requested, setRequested] = useState(false)
  const { data, isLoading, isError } = useSeriesReviews(ids, requested)
  const empty = data && data.reviews.length === 0 && data.quickTakes.length === 0 && data.unavailable.length === 0

  return (
    <>
      <Button
        size="xs"
        h={28}
        variant="light"
        leftSection={<IconMessage2 size={16} />}
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
            <Title order={3} fz="var(--type-section)">
              <Trans>Reviews</Trans>
            </Title>
          </Group>
        }
        closeButtonProps={{ 'aria-label': t`Close` }}
      >
        <Stack gap="md" pb="xl">
          <div>
            <ReviewList data={data} isLoading={isLoading} isError={isError} />
            {empty && (
              <Text size="sm" c="var(--ink-4)">
                <Trans>Nobody has reviewed this yet.</Trans>
              </Text>
            )}
          </div>
          <Stack gap={6}>
            {ids.malId != null && (
              <MoreLink href={`https://myanimelist.net/manga/${ids.malId}/_/reviews`}>
                <Trans>Read more on MyAnimeList</Trans>
              </MoreLink>
            )}
            {ids.aniListId != null && (
              <MoreLink href={`https://anilist.co/manga/${ids.aniListId}`}>
                <Trans>Read more on AniList</Trans>
              </MoreLink>
            )}
          </Stack>
        </Stack>
      </Drawer>
    </>
  )
}

function MoreLink({ href, children }: { href: string; children: ReactNode }) {
  return (
    <Anchor href={href} target="_blank" rel="noopener noreferrer" size="sm">
      <Group gap={4}>
        {children} <IconExternalLink size={16} />
      </Group>
    </Anchor>
  )
}
