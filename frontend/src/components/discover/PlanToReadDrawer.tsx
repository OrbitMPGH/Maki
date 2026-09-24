import { useState } from 'react'
import { ActionIcon, Anchor, Badge, Drawer, Group, Image, Loader, Stack, Text, Tooltip } from '@mantine/core'
import { useMediaQuery } from '@mantine/hooks'
import { notifications } from '@mantine/notifications'
import { IconInfoCircle, IconTrash } from '@tabler/icons-react'
import { Trans, useLingui } from '@lingui/react/macro'
import { msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { Link } from 'react-router-dom'
import { usePlanToRead, usePlanToReadRemove, type PlanToReadEntry } from '../../api/planToRead'
import { useRootFolders, type RecommendationItem } from '../../api/hooks'
import { formatDate } from '../../format'
import { useLabel } from '../../i18n-context'
import { DiscoverDetailModal } from './DiscoverDetailModal'

const ORIGIN_LABELS: Record<PlanToReadEntry['origin'], MessageDescriptor> = {
  taste: msg`From your taste`,
  trending: msg`Trending`,
  manual: msg`Saved from Discover`,
}

/** Builds a card-shaped item from what the entry itself carries. `DiscoverDetailModal` hydrates the
 *  rest through `useRecommendationDetail`, so every other field only needs to be present, not accurate. */
function entryAsItem(entry: PlanToReadEntry): RecommendationItem {
  return {
    providerId: String(entry.providerId),
    title: entry.title,
    coverUrl: entry.coverUrl,
    thumbUrl: null,
    thumbUrlHiDpi: null,
    year: null,
    description: null,
    status: '',
    rating: null,
    totalChapters: null,
    matchedGenres: [],
    matchedTags: [],
    authorMatch: false,
    relationKind: null,
    relatedToTitle: null,
    becauseOfTitle: null,
  }
}

export function PlanToReadDrawer({ opened, onClose }: { opened: boolean; onClose: () => void }) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const isMobile = useMediaQuery('(max-width: 47.99em)')
  const { data: entries, isLoading } = usePlanToRead()
  const remove = usePlanToReadRemove()
  const rootFolders = useRootFolders().data
  const [detailEntry, setDetailEntry] = useState<PlanToReadEntry | null>(null)

  async function removeEntry(providerId: number) {
    try {
      await remove.mutateAsync(providerId)
    } catch (error) {
      notifications.show({ color: 'var(--danger)', message: String(error) })
    }
  }

  return (
    <>
      <Drawer
        opened={opened}
        onClose={onClose}
        position={isMobile ? 'bottom' : 'right'}
        size={isMobile ? '100%' : 'sm'}
        title={t`Want to read`}
      >
        {isLoading && (
          <Group justify="center" py="xl">
            <Loader size="sm" />
          </Group>
        )}

        {!isLoading && (entries?.length ?? 0) === 0 && (
          <Text size="sm" c="var(--ink-3)" py="xl" ta="center">
            <Trans>Nothing saved yet. Swipe right or tap the bookmark on a title to add it here.</Trans>
          </Text>
        )}

        <Stack gap="sm">
          {entries?.map((entry) => (
            <Group key={entry.providerId} wrap="nowrap" gap="sm" align="flex-start">
              {entry.coverUrl ? (
                <Image src={entry.coverUrl} alt="" w={44} h={66} radius="sm" fit="cover" style={{ flexShrink: 0 }} />
              ) : (
                <div style={{ width: 44, height: 66, flexShrink: 0, borderRadius: 'var(--radius-control)', background: 'var(--surface-2)' }} />
              )}

              <Stack gap={2} style={{ flex: 1, minWidth: 0 }}>
                <Text size="sm" fw={600} lineClamp={2}>{entry.title}</Text>
                <Group gap={6} wrap="wrap">
                  <Text size="xs" c="var(--ink-3)">{renderLabel(ORIGIN_LABELS[entry.origin])}</Text>
                  <Text size="xs" c="var(--ink-4)">· {formatDate(entry.addedAtUtc)}</Text>
                </Group>
                {entry.inLibrarySeriesId != null ? (
                  <Anchor component={Link} to={`/series/${entry.inLibrarySeriesId}`} onClick={onClose} size="xs">
                    <Badge size="sm" variant="light" color="var(--ok)" style={{ cursor: 'pointer' }}>
                      <Trans>In library</Trans>
                    </Badge>
                  </Anchor>
                ) : entry.requestStatus === 'pending' ? (
                  <Badge size="sm" variant="light" color="var(--warn)">
                    <Trans>Requested</Trans>
                  </Badge>
                ) : (
                  <Badge size="sm" variant="light" color="var(--info)">
                    <Trans>Planned</Trans>
                  </Badge>
                )}
              </Stack>

              <Group gap={4} wrap="nowrap">
                <Tooltip label={t`Details`} withArrow>
                  <ActionIcon variant="subtle" aria-label={t`Details`} onClick={() => setDetailEntry(entry)}>
                    <IconInfoCircle size={16} />
                  </ActionIcon>
                </Tooltip>
                <Tooltip label={t`Remove`} withArrow>
                  <ActionIcon
                    variant="subtle" color="var(--danger)" aria-label={t`Remove`}
                    loading={remove.isPending && remove.variables === entry.providerId}
                    onClick={() => void removeEntry(entry.providerId)}
                  >
                    <IconTrash size={16} />
                  </ActionIcon>
                </Tooltip>
              </Group>
            </Group>
          ))}
        </Stack>
      </Drawer>

      <DiscoverDetailModal
        item={detailEntry ? entryAsItem(detailEntry) : null}
        inLibrarySeriesId={detailEntry?.inLibrarySeriesId}
        rootFolders={rootFolders}
        onClose={() => setDetailEntry(null)}
      />
    </>
  )
}
