import { Text } from '@mantine/core'
import { Link } from 'react-router-dom'
import { useLingui } from '@lingui/react/macro'

/** A series by name, linked to the library or opened in Discover when it has been removed. */
export function SeriesLink({
  id,
  title,
  onOpen,
}: {
  id: number | null
  title: string
  onOpen?: () => void
}) {
  const { t } = useLingui()
  if (onOpen) {
    return (
      <Text
        span
        component="button"
        type="button"
        className="stats-series-link"
        onClick={onOpen}
        aria-label={t`Open ${title} in Discover`}
      >
        {title}
      </Text>
    )
  }
  if (id === null) {
    return <Text span>{title}</Text>
  }
  return (
    <Text span component={Link} to={`/series/${id}`} className="stats-series-link">
      {title}
    </Text>
  )
}

/**
 * Cover thumbnail sized for a rank or feed row; a bordered blank when there is no cover.
 * `large` is the 44x62 variant the signals table uses, where the row is a list entry rather than a
 * one-line rank.
 */
export function SeriesThumb({ url, alt, large }: { url: string | null; alt: string; large?: boolean }) {
  const className = large ? 'stats-thumb stats-thumb-lg' : 'stats-thumb'
  if (!url) {
    return <div className={`${className} stats-thumb-empty`} aria-hidden />
  }
  return <img className={className} src={url} alt={alt} loading="lazy" />
}
