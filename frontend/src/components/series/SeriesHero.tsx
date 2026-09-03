import type { ReactNode } from 'react'
import { ActionIcon, Box, Group, Rating, Stack, Text, Title, Tooltip } from '@mantine/core'
import { IconArrowLeft, IconX } from '@tabler/icons-react'
import { Link } from 'react-router-dom'
import type { SeriesDto } from '../../api/types'
import { contentRatingVisual, seriesStatusVisual } from '../ui/status'

/**
 * status.tsx speaks in Mantine palette names; the tokens speak in meanings. One map, here, rather
 * than a class per colour, so a new slot cannot be added without also being named.
 */
const STATUS_TOKEN: Record<string, string> = {
  teal: 'ok',
  yellow: 'warn',
  blue: 'info',
  red: 'danger',
  violet: 'watched',
  gray: 'neutral',
}

/**
 * The masthead of a series page: the art, the poster, the identity, and the row of actions and
 * tabs that sit under it.
 *
 * Deliberately presentational. `actions` and `tabs` arrive as nodes so every mutation, permission
 * check and modal stays in SeriesDetailPage, which is what keeps this file readable while the page
 * it serves is not.
 *
 * The backdrop is the series' own poster filled to the band and lightly blurred, with a corner
 * falloff and two scrims over it rather than one flat wash. The recipe, and the reason a flat wash
 * looks like a smudge, are in .claude/rules/design-system.md.
 */
export function SeriesHero({
  series,
  onRate,
  actions,
  tabs,
}: {
  series: SeriesDto
  onRate: (value: number | null) => void
  actions: ReactNode
  tabs: ReactNode
}) {
  const status = seriesStatusVisual(series.status)
  const contentRating = contentRatingVisual(series.contentRating)
  const author = series.authorStory ?? series.authorArt

  // One quiet line of facts rather than a row of coloured pills: none of these is a state anyone
  // acts on, so none of them earns a colour.
  const facts = [
    series.type,
    series.year ? String(series.year) : null,
    contentRating?.label,
    series.hasAnime ? (series.animeName ?? 'Anime adaptation') : null,
    series.genres.slice(0, 5).join(', ') || null,
  ].filter(Boolean)

  const altTitles = [series.originalTitle, ...series.altTitles].filter(
    (t): t is string => !!t && t !== series.title,
  )

  return (
    <Box className="series-hero">
      {series.coverUrl && (
        <div
          className="series-hero-art"
          style={{ backgroundImage: `url(${series.coverUrl})` }}
          aria-hidden
        />
      )}
      <div className="series-hero-falloff" aria-hidden />
      <div className="series-hero-scrim-x" aria-hidden />
      <div className="series-hero-scrim-y" aria-hidden />

      <div className="series-hero-body">
        <Group gap={6} mb="md" wrap="nowrap">
          <IconArrowLeft size={16} stroke={1.9} />
          <Text component={Link} to="/library" size="sm" fw={600} c="var(--ink-2)">
            Library
          </Text>
        </Group>

        <Group align="flex-start" gap={32} wrap="nowrap" className="series-hero-row">
          {series.coverUrl && (
            <img
              className="series-hero-poster"
              src={series.coverUrl}
              alt={series.title}
            />
          )}

          <Stack gap={0} style={{ flex: 1, minWidth: 0 }}>
            <Title order={1} className="series-hero-title">
              {series.title}
            </Title>

            {author && (
              <Text size="lg" fw={500} c="var(--ink-3)" mt={7}>
                {author}
              </Text>
            )}

            <Group gap="md" mt={15} wrap="wrap">
              <span
                className="series-hero-status"
                style={{
                  color: `var(--${STATUS_TOKEN[status.color] ?? 'neutral'})`,
                  background: `var(--${STATUS_TOKEN[status.color] ?? 'neutral'}-soft)`,
                }}
              >
                <status.Icon size={14} />
                {status.label}
              </span>

              <Group gap={8} wrap="nowrap">
                <Rating
                  count={5}
                  fractions={2}
                  value={series.rating ? series.rating / 2 : 0}
                  onChange={(v) => onRate(Math.round(v * 2) || null)}
                />
                {series.rating ? (
                  <>
                    <Text size="sm" fw={600} c="var(--ink-2)" className="tnum">
                      {series.rating}/10
                    </Text>
                    <Tooltip label="Clear rating" withArrow>
                      <ActionIcon
                        size="sm"
                        variant="subtle"
                        color="gray"
                        onClick={() => onRate(null)}
                        aria-label="Clear rating"
                      >
                        <IconX size={14} />
                      </ActionIcon>
                    </Tooltip>
                  </>
                ) : (
                  <Text size="sm" c="var(--ink-4)">
                    Not rated
                  </Text>
                )}
              </Group>

              {altTitles.length > 0 && (
                <Text size="sm" c="var(--ink-3)" lineClamp={1} style={{ minWidth: 0 }}>
                  {altTitles.join(' · ')}
                </Text>
              )}
            </Group>

            {facts.length > 0 && (
              <Text size="sm" c="var(--ink-4)" mt={9}>
                {facts.join(' · ')}
              </Text>
            )}

            <Group gap="xs" mt="lg" wrap="wrap">
              {actions}
            </Group>

            <Box mt="xl">{tabs}</Box>
          </Stack>
        </Group>
      </div>
    </Box>
  )
}
