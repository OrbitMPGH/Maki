import { Badge, Group, Tooltip } from '@mantine/core'
import type { MouseEvent } from 'react'
import type { MetadataLink } from '../api/types'

/** Display label + color for each known metadata site key. */
const SITES: Record<string, { label: string; short: string; color: string }> = {
  mangabaka: { label: 'MangaBaka', short: 'MB', color: 'orange' },
  anilist: { label: 'AniList', short: 'AL', color: 'blue' },
  myanimelist: { label: 'MyAnimeList', short: 'MAL', color: 'indigo' },
  mangaupdates: { label: 'MangaUpdates', short: 'MU', color: 'grape' },
  mangadex: { label: 'MangaDex', short: 'MD', color: 'orange.7' },
}

function siteInfo(site: string) {
  return SITES[site] ?? { label: site, short: site.slice(0, 2).toUpperCase(), color: 'gray' }
}

/**
 * Clickable external metadata links (MangaBaka / AniList / MAL / …). Each badge
 * opens the site in a new tab. Rendered as buttons (not anchors) so it can sit
 * inside a clickable card without invalid anchor nesting; the click is stopped
 * from bubbling to the parent. `compact` renders tiny badges for grid cards.
 */
export function MetadataLinks({
  links,
  compact = false,
}: {
  links: MetadataLink[]
  compact?: boolean
}) {
  if (links.length === 0) return null

  const open = (e: MouseEvent, url: string) => {
    e.preventDefault()
    e.stopPropagation()
    window.open(url, '_blank', 'noopener,noreferrer')
  }

  return (
    <Group gap={compact ? 4 : 'xs'} wrap="wrap">
      {links.map((link) => {
        const info = siteInfo(link.site)
        return (
          <Tooltip key={link.site} label={`Open on ${info.label}`} withArrow openDelay={300}>
            <Badge
              size={compact ? 'xs' : 'sm'}
              variant="light"
              color={info.color}
              style={{ cursor: 'pointer' }}
              role="link"
              tabIndex={0}
              aria-label={`Open on ${info.label}`}
              onClick={(e) => open(e, link.url)}
              onKeyDown={(e) => {
                if (e.key === 'Enter' || e.key === ' ') {
                  e.preventDefault()
                  e.stopPropagation()
                  window.open(link.url, '_blank', 'noopener,noreferrer')
                }
              }}
            >
              {compact ? info.short : info.label}
            </Badge>
          </Tooltip>
        )
      })}
    </Group>
  )
}
