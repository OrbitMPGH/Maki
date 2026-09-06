import type { ReactNode } from 'react'
import { Link } from 'react-router-dom'
import { Anchor, Paper, Text, Title } from '@mantine/core'
import type { MangaBakaDetail } from '../../api/hooks'

/**
 * The facts about a series that are read rather than scanned: who made it, who prints it, and the
 * publication numbers the band doesn't carry.
 *
 * Label over value rather than the series page's two-column record grid: this sits in a rail about
 * a fifth of the modal wide, where a 108px label column and a right-aligned value leave publisher
 * lists a word per line.
 */
export function DiscoverGlance({
  detail,
  onNavigate,
}: {
  detail: MangaBakaDetail
  /** Closes the modal before a creator link navigates out from under it. */
  onNavigate: () => void
}) {
  const published =
    detail.year != null
      ? `${detail.year}${detail.status === 'Ongoing' ? ' – present' : ''}`
      : null

  return (
    <Paper withBorder radius="lg" p="md">
      <Title order={3} fz={16}>
        At a glance
      </Title>

      <div className="detail-records">
        {detail.authors.length > 0 && (
          <Record label="Story">
            <Creators values={detail.authors} role="author" onNavigate={onNavigate} />
          </Record>
        )}
        {detail.artists.length > 0 && (
          <Record label="Art">
            <Creators values={detail.artists} role="artist" onNavigate={onNavigate} />
          </Record>
        )}
        {detail.publishers.length > 0 && (
          <Record label="Publishers">
            <Creators values={detail.publishers} role="studio" onNavigate={onNavigate} />
          </Record>
        )}
        {detail.type && <Record label="Type">{detail.type}</Record>}
        {published && (
          <Record label="Published">
            <span className="tnum">{published}</span>
          </Record>
        )}
        {detail.totalChapters != null && (
          <Record label="Chapters">
            <span className="tnum">{detail.totalChapters}</span>
          </Record>
        )}
        {detail.finalVolume != null && (
          <Record label="Volumes">
            <span className="tnum">{detail.finalVolume}</span>
          </Record>
        )}
        {detail.contentRating && (
          <Record label="Content rating">
            <span style={{ textTransform: 'capitalize' }}>{detail.contentRating}</span>
          </Record>
        )}
      </div>
    </Paper>
  )
}

function Record({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="detail-record">
      <Text size="xs" c="var(--ink-4)">
        {label}
      </Text>
      <Text size="sm" c="var(--ink-2)">
        {children}
      </Text>
    </div>
  )
}

/**
 * Names that link through to everything that person or studio made.
 *
 * <p>
 * Navigating closes this modal first. On Discover it can already be the second layer, opened from
 * behind `FeedExpandModal` and carrying an explicit zIndex to survive that; a creator view stacked
 * on top would be a third. A route is also linkable, which a modal is not, and Back brings the
 * results underneath straight out of the query cache.
 * </p>
 */
function Creators({
  values,
  role,
  onNavigate,
}: {
  values: string[]
  role: 'author' | 'artist' | 'studio'
  onNavigate: () => void
}) {
  return (
    <>
      {values.map((value, i) => (
        <span key={value}>
          {i > 0 && ', '}
          <Anchor
            component={Link}
            to={`/creator/${encodeURIComponent(value)}?role=${role}`}
            onClick={onNavigate}
            inherit
          >
            {value}
          </Anchor>
        </span>
      ))}
    </>
  )
}
