import { memo } from 'react'
import { useNavigate } from 'react-router-dom'
import { ActionIcon, Badge, Group, Text, Tooltip } from '@mantine/core'
import { IconCheck, IconPlus, IconStar } from '@tabler/icons-react'
import type { RecommendationItem } from '../../api/hooks'

/**
 * Poster cards for catalogue (MangaBaka) items, and the horizontal rail that lays them out.
 *
 * Lives here rather than in DiscoverPage because three surfaces render these — Discover, the
 * series page's related rail and the Home dashboard — and importing them from a page module
 * dragged that page's filter panel and its Mantine sliders into every consumer's bundle.
 *
 * For *owned* series use `components/ui/CoverCard` instead; these take a catalogue item, which
 * has no library id, no download counts and no read progress.
 */

function reasonFor(item: RecommendationItem): string {
  if (item.relationKind) {
    return `${item.relationKind} of ${item.relatedToTitle}`
  }
  const parts: string[] = []
  if (item.authorMatch) parts.push('same author')
  const because = [...item.matchedGenres, ...item.matchedTags].slice(0, 3)
  if (because.length > 0) parts.push(because.join(', '))
  // Semantic picks name the seed whose feel drove them; genre-only hits keep "Because:".
  if (item.becauseOfTitle) {
    const feel = `Feels like ${item.becauseOfTitle}`
    return parts.length > 0 ? `${feel} · ${parts.join(' · ')}` : feel
  }
  return parts.length > 0 ? `Because: ${parts.join(' · ')}` : 'Similar feel'
}

/** Poster-forward Discover card. Cover art is the hero; a bottom scrim carries the
 *  reason line, title and meta, and a corner control quick-opens (or navigates when owned).
 *
 *  Memoized because Discover mounts hundreds of these at once: without it, any state change on
 *  an ancestor (a keystroke in the search box, say) reconciles every card on the page. */
export const RecommendationCard = memo(function RecommendationCard({
  item,
  inLibrarySeriesId,
  onOpen,
  reasonOverride,
}: {
  item: RecommendationItem
  /** Library series id if already owned (shows a persistent "in library" check); null otherwise. */
  inLibrarySeriesId: number | null
  onOpen: (item: RecommendationItem) => void
  /** Overrides the reason line: a string replaces it, `null` hides it. Omit for the default. */
  reasonOverride?: string | null
}) {
  const navigate = useNavigate()
  const owned = inLibrarySeriesId != null
  const reason = reasonOverride !== undefined ? reasonOverride : reasonFor(item)

  return (
    <div
      className="cover-card discover-card"
      role="button"
      tabIndex={0}
      aria-label={item.title}
      onClick={() => onOpen(item)}
      onKeyDown={(e) => {
        if (e.key === 'Enter' || e.key === ' ') {
          e.preventDefault()
          onOpen(item)
        }
      }}
    >
      <div className="cover-poster">
        {item.coverUrl ? (
          <img src={item.coverUrl} alt={item.title} loading="lazy" />
        ) : (
          <div className="cover-placeholder">{item.title}</div>
        )}
        <div className="cover-scrim" />

        {item.rating != null && (
          <Badge
            size="sm"
            variant="filled"
            color="dark.9"
            leftSection={<IconStar size={10} style={{ color: '#f5c518' }} />}
            style={{ position: 'absolute', top: 8, left: 8 }}
          >
            {(item.rating / 10).toFixed(1)}
          </Badge>
        )}

        {owned ? (
          <Tooltip label="In library — open" withArrow>
            <ActionIcon
              className="discover-corner"
              variant="filled"
              color="teal"
              radius="xl"
              size="md"
              aria-label="View in library"
              onClick={(e) => {
                e.stopPropagation()
                navigate(`/series/${inLibrarySeriesId}`)
              }}
            >
              <IconCheck size={16} />
            </ActionIcon>
          </Tooltip>
        ) : (
          <Tooltip label="View & add" withArrow>
            <ActionIcon
              className="discover-corner"
              data-add="true"
              variant="filled"
              color="brand"
              radius="xl"
              size="md"
              aria-label="View and add"
              onClick={(e) => {
                e.stopPropagation()
                onOpen(item)
              }}
            >
              <IconPlus size={16} />
            </ActionIcon>
          </Tooltip>
        )}

        <div className="discover-meta">
          {reason && (
            <span className="discover-reason" title={reason}>
              {reason}
            </span>
          )}
          <Text fw={650} size="sm" c="white" lineClamp={2} lh={1.2} title={item.title}>
            {item.title}
          </Text>
          <Group gap={5} mt={5} wrap="nowrap">
            {item.year && (
              <Text size="xs" c="gray.4" className="tnum">
                {item.year}
              </Text>
            )}
            <Text size="xs" c="gray.4" tt="capitalize" lineClamp={1}>
              · {item.status}
            </Text>
            {item.totalChapters && (
              <Text size="xs" c="gray.4" style={{ whiteSpace: 'nowrap' }}>
                · {item.totalChapters} ch
              </Text>
            )}
          </Group>
        </div>
      </div>
    </div>
  )
})

/** A single horizontal-scroll rail of poster cards. */
export function DiscoverRailRow({
  items,
  seriesIdFor,
  onOpen,
}: {
  items: RecommendationItem[]
  seriesIdFor: (item: RecommendationItem) => number | null
  onOpen: (item: RecommendationItem) => void
}) {
  return (
    <div className="discover-rail">
      {items.map((item) => (
        <div key={item.providerId} className="discover-rail-item">
          <RecommendationCard
            item={item}
            inLibrarySeriesId={seriesIdFor(item)}
            onOpen={onOpen}
            reasonOverride={null}
          />
        </div>
      ))}
    </div>
  )
}
