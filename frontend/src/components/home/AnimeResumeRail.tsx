import { memo, type ReactNode } from 'react'
import { Link } from 'react-router-dom'
import { ActionIcon, Button, Group, Menu, Modal, SimpleGrid, Text, ThemeIcon } from '@mantine/core'
import { IconClock, IconDeviceTv, IconDots, IconEyeOff, IconPlus } from '../../lib/icons'
import { useDismissFromAnimeRail, useHomeFromAnimeAll, type HomeAnimeResumeItem } from '../../api/animeResume'
import { errorText } from '../../api/errorText'
import { notifications } from '../../lib/toast'
import type { RecommendationItem } from '../../api/hooks'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { PosterSkeletons } from '../CatalogueBrowser'
import { EmptyState } from '../ui/EmptyState'
import { Rail } from '../ui/Rail'
import { DensityControl, useDensityPref } from '../ui/viewPrefs'
import { chapterLabel, serverChapterLabel } from '../../format'

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
        <div key={item.mangaBakaId} className="discover-rail-item reading-card">
          <AnimeResumeCard item={item} onOpen={onOpen} />
          <AnimeResumeCardMenu item={item} />
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
          <Text
            span
            fz="var(--mantine-h4-font-size)"
            fw="var(--mantine-h4-font-weight)"
            lh="var(--mantine-h4-line-height)"
            ff="var(--mantine-font-family-headings)"
          >
            <Trans>Continue from the anime</Trans>
          </Text>
        </Group>
      }
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
              <div key={item.mangaBakaId} className="reading-card">
                <AnimeResumeCard item={item} onOpen={onOpen} />
                <AnimeResumeCardMenu item={item} />
              </div>
            ))}
          </SimpleGrid>
        </>
      )}
    </Modal>
  )
}

/**
 * Takes a card off the rail: until a newly watched season moves the frontier (another season is
 * coming and the reader would rather watch it first), or for good (they liked the anime and have
 * no interest in the manga). A sibling of the card's link, like `ReadingCardMenu`, so a click in
 * the portalled dropdown never bubbles into the link.
 */
function AnimeResumeCardMenu({ item }: { item: HomeAnimeResumeItem }) {
  const { t } = useLingui()
  const dismiss = useDismissFromAnimeRail()
  const { mangaBakaId, seriesTitle, coveredTo } = item

  // mutateAsync for the same reason as ReadingCardMenu: the optimistic update unmounts this card.
  const remove = async (untilNewSeason: boolean) => {
    try {
      await dismiss.mutateAsync({ mangaBakaId, coveredTo: untilNewSeason ? coveredTo : null })
    } catch (error) {
      const reason = errorText(error)
      notifications.show({ color: 'var(--danger)', message: t`Could not remove ${seriesTitle}: ${reason}` })
      return
    }
    const id = notifications.show({
      autoClose: 8000,
      message: (
        <Group gap="xs" wrap="nowrap" justify="space-between">
          <Text size="sm">
            {untilNewSeason ? (
              <Trans>Hid {seriesTitle}. It comes back when you finish a new season.</Trans>
            ) : (
              <Trans>Removed {seriesTitle}. It won't be suggested here again.</Trans>
            )}
          </Text>
          <Button
            size="xs"
            variant="subtle"
            style={{ flexShrink: 0 }}
            onClick={() => {
              notifications.hide(id)
              dismiss.mutateAsync({ mangaBakaId, restore: true }).catch((error: unknown) => {
                const reason = errorText(error)
                notifications.show({ color: 'var(--danger)', message: t`Could not restore ${seriesTitle}: ${reason}` })
              })
            }}
          >
            <Trans>Undo</Trans>
          </Button>
        </Group>
      ),
    })
  }

  return (
    <Menu withinPortal position="bottom-end" shadow="md">
      <Menu.Target>
        <ActionIcon
          className="reading-card-menu"
          variant="filled"
          radius="xl"
          size={28}
          aria-label={t`More options for ${seriesTitle}`}
        >
          <IconDots size={16} />
        </ActionIcon>
      </Menu.Target>
      <Menu.Dropdown>
        <Menu.Item leftSection={<IconClock size={16} />} onClick={() => void remove(true)}>
          <Trans>Hide until a new season</Trans>
        </Menu.Item>
        <Menu.Item leftSection={<IconEyeOff size={16} />} onClick={() => void remove(false)}>
          <Trans>Not reading this one</Trans>
        </Menu.Item>
      </Menu.Dropdown>
    </Menu>
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
            <span>{item.resumeChapterLabel ? serverChapterLabel(item.resumeChapterLabel) : chapterLabel(next)}</span>
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
