import type { ReactNode } from 'react'
import { Text } from '@mantine/core'
import { Link } from 'react-router-dom'
import { Thumb } from './ui/Thumb'

/**
 * The series a table row belongs to: its cover, its title and, under it, the chapter, release or
 * file. Without a `seriesId` (a tracker's title no library series matched) it shows the title plain.
 */
export function SeriesCell({
  seriesId,
  title,
  coverUrl,
  detail,
  small,
}: {
  seriesId: number | null
  title: string
  coverUrl: string | null
  detail?: ReactNode
  small?: boolean
}) {
  const href = seriesId != null ? `/series/${seriesId}` : null
  const cover = <Thumb src={coverUrl} size={small ? 'sm' : 'md'} aria-hidden />
  return (
    <div className="series-cell" data-small={small || undefined}>
      {href ? (
        <Link to={href} className="series-cell-link" tabIndex={-1} aria-hidden>
          {cover}
        </Link>
      ) : (
        cover
      )}
      <div className="series-cell-text">
        {href ? (
          <Text component={Link} to={href} className="series-title" lineClamp={2}>
            {title}
          </Text>
        ) : (
          <Text className="series-title" lineClamp={2}>
            {title}
          </Text>
        )}
        {detail != null && (
          <Text component="div" size="xs" c="var(--ink-3)" className="tnum" lineClamp={2}>
            {detail}
          </Text>
        )}
      </div>
    </div>
  )
}
