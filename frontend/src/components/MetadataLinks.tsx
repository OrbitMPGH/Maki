import { Group, Tooltip } from '@mantine/core'
import type { MouseEvent, ReactNode } from 'react'
import { useLingui } from '@lingui/react/macro'
import type { MetadataLink } from '../api/types'
import { siteInfo } from '../lib/metadataSites'
import { MetadataSiteIcon } from './MetadataSiteIcon'

/**
 * Clickable external metadata links (MangaBaka / AniList / MAL / …), one bordered chip per site
 * with the site's score inside when there is one. Each opens the site in a new tab. Rendered as
 * buttons (not anchors) so it can sit inside a clickable card without invalid anchor nesting; the
 * click is stopped from bubbling to the parent.
 */
export function MetadataLinks({
  links,
  extra,
}: {
  links: MetadataLink[]
  /** Accepted for old call sites; chips are the only rendering. */
  chips?: boolean
  /** Trailing items in the same row, such as a rated site that has no link. */
  extra?: ReactNode
}) {
  const { t } = useLingui()

  if (links.length === 0 && !extra) return null

  const open = (e: MouseEvent, url: string) => {
    e.preventDefault()
    e.stopPropagation()
    window.open(url, '_blank', 'noopener,noreferrer')
  }

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
