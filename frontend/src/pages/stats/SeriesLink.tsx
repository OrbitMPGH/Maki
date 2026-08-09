import { Text } from '@mantine/core'
import { Link } from 'react-router-dom'

/**
 * A series by name, linked when it still exists.
 *
 * Stats lists are built from the event log, which keeps a denormalized title for series that have
 * since been removed. Those rows still belong in the numbers, so they render as plain text rather
 * than a link into a 404.
 */
export function SeriesLink({ id, title }: { id: number | null; title: string }) {
  if (id === null) {
    return <Text span>{title}</Text>
  }
  return (
    <Text span component={Link} to={`/series/${id}`} className="stats-series-link">
      {title}
    </Text>
  )
}

/** Cover thumbnail sized for a rank or feed row; a bordered blank when there is no cover. */
export function SeriesThumb({ url, alt }: { url: string | null; alt: string }) {
  if (!url) {
    return <div className="stats-thumb stats-thumb-empty" aria-hidden />
  }
  return <img className="stats-thumb" src={url} alt={alt} loading="lazy" />
}
