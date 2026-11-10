import { ActionIcon, Group, Tooltip } from '@mantine/core'
import type { MouseEvent, ReactNode } from 'react'
import { useLingui } from '@lingui/react/macro'
import type { MetadataLink } from '../api/types'
import { siteInfo } from '../lib/metadataSites'
import { MetadataSiteIcon } from './MetadataSiteIcon'

/**
 * Clickable external metadata links (MangaBaka / AniList / MAL / …), rendered as
 * small square marks with the site's score beside each when there is one. Each opens the site in a new tab. Rendered as buttons (not
 * anchors) so it can sit inside a clickable card without invalid anchor nesting;
 * the click is stopped from bubbling to the parent. `compact` renders smaller
 * icons for grid cards.
 */
export function MetadataLinks({
  links,
  compact = false,
  chips = false,
  extra,
}: {
  links: MetadataLink[]
  compact?: boolean
  /**
   * One button per site with its score inside, as on the Discover detail header, instead of a
   * square mark with the score beside it.
   */
  chips?: boolean
  /** Trailing items in the same row, such as a rated site that has no link. */
  extra?: ReactNode
}) {
  const { t } = useLingui()

  if (links.length === 0) return null

  const open = (e: MouseEvent, url: string) => {
    e.preventDefault()
    e.stopPropagation()
    window.open(url, '_blank', 'noopener,noreferrer')
  }

  if (chips) {
    return (
      <Group gap={6} wrap="wrap" align="center">
        {links.map((link) => {
          const info = siteInfo(link.site)
          const { label } = info
          const score = link.score != null ? String(link.score) : null
          const tip = link.votes != null ? t`${label}, ${link.votes.toLocaleString()} votes` : label
          return (
            <Tooltip key={link.site} label={tip} withArrow openDelay={300} zIndex={1001}>
              <button
                type="button"
                className="site-chip"
                data-bare={score ? undefined : true}
                role="link"
                aria-label={score ? t`Open on ${label}, rated ${score}` : t`Open on ${label}`}
                onClick={(e) => open(e, link.url)}
              >
                <MetadataSiteIcon site={link.site} monogram={info.short} size={15} />
                {score && <span className="tnum">{score}</span>}
              </button>
            </Tooltip>
          )
        })}
        {extra}
      </Group>
    )
  }

  return (
    <Group gap={compact ? 8 : 12} wrap="wrap" align="center">
      {links.map((link) => {
        const info = siteInfo(link.site)
        const { label } = info
        const score = link.score != null ? String(link.score) : null
        const tip = link.votes != null ? t`${label}, ${link.votes.toLocaleString()} votes` : label
        return (
          <span key={link.site} className="site-mark-item">
            <Tooltip label={tip} withArrow openDelay={300}>
              <ActionIcon
                size={compact ? 22 : 26}
                variant="default"
                className="site-mark"
                role="link"
                tabIndex={0}
                aria-label={t`Open on ${label}`}
                onClick={(e) => open(e, link.url)}
                onKeyDown={(e) => {
                  if (e.key === 'Enter' || e.key === ' ') {
                    e.preventDefault()
                    e.stopPropagation()
                    window.open(link.url, '_blank', 'noopener,noreferrer')
                  }
                }}
              >
                <MetadataSiteIcon site={link.site} monogram={info.short} size={compact ? 12 : 14} />
              </ActionIcon>
            </Tooltip>
            {score && <span className="site-mark-score tnum">{score}</span>}
          </span>
        )
      })}
    </Group>
  )
}
