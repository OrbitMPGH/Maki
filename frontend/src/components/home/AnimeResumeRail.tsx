import { memo, type ReactNode } from 'react'
import { Link } from 'react-router-dom'
import { Group, Modal, SimpleGrid, Text, ThemeIcon, Title } from '@mantine/core'
import { IconDeviceTv, IconPlus } from '@tabler/icons-react'
import { useHomeFromAnimeAll, type HomeAnimeResumeItem } from '../../api/animeResume'
import type { RecommendationItem } from '../../api/hooks'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { PosterSkeletons } from '../CatalogueBrowser'
import { EmptyState } from '../ui/EmptyState'
import { Rail } from '../ui/Rail'
import { DensityControl, useDensityPref } from '../ui/viewPrefs'

/**
 * Horizontal rail of series whose anime the reader finished but the manga hasn't caught up to.
 * Same card markup as {@link RecentlyAddedRail}, but the chapter chip names the resume chapter
 * instead of the newest one, and there is no Read button: the resume chapter may not even be
 * downloaded yet, so a library card only ever opens the series page.
 *
 * A card with no library copy opens the Discover modal instead, whose Add can tick the anime's
 * chapters off in the same step.
 */
export function AnimeResumeRail({
  items,
  onOpen,
}: {
  items: HomeAnimeResumeItem[]
  onOpen: (item: RecommendationItem) => void
}) {
  return (
    <Rail>
      {items.map((item) => (
        <div key={item.seriesId ?? `mb-${item.catalogue?.providerId}`} className="discover-rail-item">
          <AnimeResumeCard item={item} onOpen={onOpen} />
        </div>
      ))}
    </Rail>
  )
}

/** The rail's "Show more": every series it matches, as a grid, with the rail's own cards. */
export function AnimeResumeExpandModal({
  opened,
  onClose,
  onOpen,
}: {
  opened: boolean
  onClose: () => void
  onOpen: (item: RecommendationItem) => void
}) {
  const { t } = useLingui()
  const { data, isLoading, isError, refetch } = useHomeFromAnimeAll(opened)
  const { density, setDensity, cols } = useDensityPref('fromanime-expand')
  const items = data?.items

  return (
    <Modal
      opened={opened}
      onClose={onClose}
      fullScreen
      title={
        <Group gap="xs">
          <ThemeIcon variant="light" color="brand" size="md" radius="md">
            <IconDeviceTv size={16} />
          </ThemeIcon>
          <Title order={4}>
            <Trans>Continue from the anime</Trans>
          </Title>
        </Group>
      }
      styles={{ body: { paddingTop: 'var(--mantine-spacing-md)' } }}
    >
      {isError ? (
        <EmptyState title={t`Couldn't load this rail`} actionLabel={t`Retry`} onAction={() => void refetch()} />
      ) : isLoading || !items ? (
        <PosterSkeletons density={density} />
      ) : (
        <>
          <Group justify="space-between" mb="sm">
            <Text c="var(--ink-3)" size="sm">
              <Plural value={items.length} one="# title" other="# titles" />
            </Text>
            <DensityControl value={density} onChange={setDensity} />
          </Group>
          <SimpleGrid cols={cols} spacing="md">
            {items.map((item) => (
              <AnimeResumeCard key={item.seriesId ?? `mb-${item.catalogue?.providerId}`} item={item} onOpen={onOpen} />
            ))}
          </SimpleGrid>
        </>
      )}
    </Modal>
  )
}

// Memoized: `item` keeps its reference across renders, so unrelated Home state does not re-render the cards.
const AnimeResumeCard = memo(function AnimeResumeCard({
  item,
  onOpen,
}: {
  item: HomeAnimeResumeItem
  onOpen: (item: RecommendationItem) => void
}) {
  const { t } = useLingui()
  const title = item.seriesTitle

  if (item.seriesId == null && item.catalogue) {
    const catalogue = item.catalogue
    return (
      <div className="cover-card discover-card">
        <button
          type="button"
          className="discover-card-action"
          aria-label={t`View and add ${title}`}
          onClick={() => onOpen(catalogue)}
        />
        <AnimeResumePoster item={item}>
          <span className="discover-corner" data-add="true" data-tip={t`View and add`} aria-hidden="true">
            <IconPlus size={16} />
          </span>
        </AnimeResumePoster>
      </div>
    )
  }

  return (
    <Link to={`/series/${item.seriesId}`} className="cover-card">
      <AnimeResumePoster item={item} />
    </Link>
  )
})

function AnimeResumePoster({ item, children }: { item: HomeAnimeResumeItem; children?: ReactNode }) {
  const next = Math.floor(item.coveredTo) + 1

  return (
    <div className="cover-poster">
      {item.coverUrl ? (
        <img src={item.coverUrl} alt="" loading="lazy" decoding="async" />
      ) : (
        <div className="cover-placeholder" aria-hidden>{item.seriesTitle}</div>
      )}
      <div className="cover-scrim" />

      <div className="cover-corners">
        <div className="cover-corner cover-corner-left">
          <span className="cover-chapter" data-tip={item.animeTitle}>
            <span>{item.resumeChapterLabel ?? <Trans>ch. {next}</Trans>}</span>
          </span>
        </div>
      </div>
      {children}

      <div className="cover-meta">
        <span className="cover-title" title={item.seriesTitle}>
          {item.seriesTitle}
        </span>
        <div className="cover-row">
          <span>
            <Trans>Anime ends at ch. {item.coveredTo}</Trans>
          </span>
        </div>
      </div>
    </div>
  )
}
