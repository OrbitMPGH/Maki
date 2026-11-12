import { useState } from 'react'
import { Link } from 'react-router-dom'
import { Anchor, Button, Group, Modal, Stack, Text } from '@mantine/core'
import { IconBell, IconChevronRight, IconSettings } from '../../lib/icons'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { useDiscoverFeed, type CatalogueCredit, type DiscoverRail, type RecommendationItem } from '../../api/hooks'
import {
  FOLLOWING_RAIL_KEY,
  FOLLOWING_RAIL_MONTHS,
  followingFeedRequest,
  sameCredit,
  useFollowedCreators,
  useSaveFollowedCreators,
} from '../../api/following'
import { CREDIT_ROLE_LABELS } from '../CreditPicker'
import { DiscoverRailRow, type OpenItem } from '../ui/DiscoverRail'
import { sectionSource } from '../../api/shortlist'
import { EmptyState } from '../ui/EmptyState'
import { ModalFooter, ModalHeading } from '../ui/ModalShell'
import { RailSkeleton } from '../ui/RailSkeleton'
import { SectionHeader } from '../ui/SectionHeader'
import { useLabel } from '../../i18n-context'

export function creatorHref(c: CatalogueCredit) {
  return `/creator/${encodeURIComponent(c.name)}${c.role ? `?role=${c.role}` : ''}`
}

/**
 * The last year's catalogue titles credited to anyone the reader follows, minus what they own or have
 * liked, disliked, hidden or marked seen. Renders
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
  onOpen: OpenItem
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
    excludeInteracted: true,
    releasedWithinMonths: FOLLOWING_RAIL_MONTHS,
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
              leftSection={<IconSettings size={16} />}
              onClick={() => setManaging(true)}
            >
              <Trans>Manage</Trans>
            </Button>
            <Button
              variant="subtle"
              size="xs"
              rightSection={<IconChevronRight size={16} />}
              onClick={() => onShowMore(expanded)}
            >
              <Trans>Show more</Trans>
            </Button>
          </Group>
        }
      />
      {items && items.length > 0 ? (
        <DiscoverRailRow items={items} seriesIdFor={seriesIdFor} onOpen={onOpen} savedFrom={sectionSource('following')} />
      ) : (
        <Text c="var(--ink-3)" size="sm">
          <Trans>Nothing new from them in the last {FOLLOWING_RAIL_MONTHS} months that you have not seen already.</Trans>
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
    <Modal
      opened={opened}
      onClose={onClose}
      title={
        <ModalHeading
          title={t`Creators you follow`}
          description={<Trans>New series from them show up on Discover.</Trans>}
        />
      }
    >
      <Stack gap="sm">
        {creators.length === 0 ? (
          <EmptyState
            compact
            mood="asleep"
            title={t`No creators followed`}
            description={<Trans>Follow one from a creator page.</Trans>}
          />
        ) : (
          <div className="modal-list">
            {creators.map((c) => {
              const name = c.name
              return (
                <div key={`${c.role ?? ''}/${name}`} className="modal-list-row">
                  <Anchor component={Link} to={creatorHref(c)} onClick={onClose} size="sm" fw={600} style={{ flex: 1, minWidth: 0 }}>
                    {name}
                  </Anchor>
                  {c.role && <span className="modal-role-chip">{renderLabel(CREDIT_ROLE_LABELS[c.role] ?? c.role)}</span>}
                  <Button
                    variant="subtle"
                    color="var(--neutral)"
                    size="compact-sm"
                    aria-label={t`Unfollow ${name}`}
                    disabled={save.isPending}
                    onClick={() => save.mutate(creators.filter((other) => !sameCredit(other, c)))}
                  >
                    <Trans>Unfollow</Trans>
                  </Button>
                </div>
              )
            })}
          </div>
        )}
        <ModalFooter status={<Plural value={creators.length} one="# creator" other="# creators" />}>
          <Button variant="default" onClick={onClose}>
            <Trans>Close</Trans>
          </Button>
        </ModalFooter>
      </Stack>
    </Modal>
  )
}
