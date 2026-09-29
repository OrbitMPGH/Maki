import { useState } from 'react'
import { Link } from 'react-router-dom'
import { ActionIcon, Anchor, Button, Group, Modal, Stack, Text } from '@mantine/core'
import { IconBell, IconChevronRight, IconSettings, IconX } from '@tabler/icons-react'
import { Trans, useLingui } from '@lingui/react/macro'
import { useDiscoverFeed, type CatalogueCredit, type DiscoverRail, type RecommendationItem } from '../../api/hooks'
import {
  FOLLOWING_RAIL_KEY,
  followingFeedRequest,
  sameCredit,
  useFollowedCreators,
  useSaveFollowedCreators,
} from '../../api/following'
import { CREDIT_ROLE_LABELS } from '../CreditPicker'
import { DiscoverRailRow } from '../ui/DiscoverRail'
import { RailSkeleton } from '../ui/RailSkeleton'
import { SectionHeader } from '../ui/SectionHeader'
import { useLabel } from '../../i18n-context'

export function creatorHref(c: CatalogueCredit) {
  return `/creator/${encodeURIComponent(c.name)}${c.role ? `?role=${c.role}` : ''}`
}

/**
 * The newest catalogue titles credited to anyone the reader follows, owned series left out. Renders
 * nothing for somebody who follows nobody: the follow button on a creator page is how it starts.
 */
export function FollowingRail({
  enabled,
  limit,
  seriesIdFor,
  onOpen,
  onShowMore,
}: {
  enabled: boolean
  limit: number
  seriesIdFor: (item: RecommendationItem) => number | null
  onOpen: (item: RecommendationItem) => void
  /** Opens the rail's expanded view, with its creators loaded as the filter. */
  onShowMore: (rail: DiscoverRail) => void
}) {
  const { t } = useLingui()
  const [managing, setManaging] = useState(false)
  const { data: followed, isLoading: followedLoading } = useFollowedCreators(enabled)
  const creators = followed?.creators ?? []
  const request = enabled ? followingFeedRequest(creators, limit) : null
  const { data: items, isLoading } = useDiscoverFeed(request, true)

  if (!enabled) return null
  if (followedLoading || (request && isLoading)) return <RailSkeleton title />
  if (creators.length === 0) return null

  const title = t`New from creators you follow`
  const expanded: DiscoverRail = {
    key: FOLLOWING_RAIL_KEY,
    title,
    feed: 'Popular',
    genre: null,
    items: items ?? [],
    filters: { credits: creators },
    sort: 'newest',
    excludeOwned: true,
  }

  return (
    <div>
      <SectionHeader
        icon={IconBell}
        title={title}
        count={items?.length}
        action={
          <Group gap={4} wrap="nowrap">
            <Button
              variant="subtle"
              size="xs"
              leftSection={<IconSettings size={14} />}
              onClick={() => setManaging(true)}
            >
              <Trans>Manage</Trans>
            </Button>
            <Button
              variant="subtle"
              size="xs"
              rightSection={<IconChevronRight size={14} />}
              onClick={() => onShowMore(expanded)}
            >
              <Trans>Show more</Trans>
            </Button>
          </Group>
        }
      />
      {items && items.length > 0 ? (
        <DiscoverRailRow items={items} seriesIdFor={seriesIdFor} onOpen={onOpen} />
      ) : (
        <Text c="var(--ink-3)" size="sm">
          <Trans>Everything they have in the catalogue is already in your library.</Trans>
        </Text>
      )}
      <FollowingManager opened={managing} creators={creators} onClose={() => setManaging(false)} />
    </div>
  )
}

function FollowingManager({
  opened,
  creators,
  onClose,
}: {
  opened: boolean
  creators: CatalogueCredit[]
  onClose: () => void
}) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const save = useSaveFollowedCreators()

  return (
    <Modal opened={opened} onClose={onClose} title={t`Creators you follow`}>
      <Stack gap="xs">
        {creators.length === 0 && (
          <Text c="var(--ink-3)" size="sm">
            <Trans>You follow nobody yet. Open a creator or studio and press Follow.</Trans>
          </Text>
        )}
        {creators.map((c) => {
          const name = c.name
          return (
            <Group key={`${c.role ?? ''}/${name}`} justify="space-between" wrap="nowrap">
              <Anchor component={Link} to={creatorHref(c)} onClick={onClose} size="sm">
                {name}
                {c.role && (
                  <Text span c="var(--ink-3)" size="sm">
                    {' '}
                    ({renderLabel(CREDIT_ROLE_LABELS[c.role] ?? c.role)})
                  </Text>
                )}
              </Anchor>
              <ActionIcon
                variant="subtle"
                color="gray"
                aria-label={t`Unfollow ${name}`}
                title={t`Unfollow`}
                disabled={save.isPending}
                onClick={() => save.mutate(creators.filter((other) => !sameCredit(other, c)))}
              >
                <IconX size={16} />
              </ActionIcon>
            </Group>
          )
        })}
      </Stack>
    </Modal>
  )
}
