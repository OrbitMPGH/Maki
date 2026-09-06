import { Paper, Title } from '@mantine/core'
import type { MangaBakaDetail } from '../../api/hooks'
import { Creators, DetailRecord } from './DiscoverRecord'

/**
 * The facts about a series that are read rather than scanned: who made it, who prints it, and the
 * publication numbers the band doesn't carry.
 *
 * Label over value rather than the series page's two-column record grid: this sits in a rail about
 * a fifth of the modal wide, where a label column and a right-aligned value leave publisher lists
 * a word per line.
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
    detail.year != null ? `${detail.year}${detail.status === 'Ongoing' ? ' – present' : ''}` : null

  return (
    <Paper withBorder radius="lg" p="md">
      <Title order={3} fz={16}>
        At a glance
      </Title>

      <div className="detail-records">
        {detail.authors.length > 0 && (
          <DetailRecord label="Story">
            <Creators values={detail.authors} role="author" onNavigate={onNavigate} />
          </DetailRecord>
        )}
        {detail.artists.length > 0 && (
          <DetailRecord label="Art">
            <Creators values={detail.artists} role="artist" onNavigate={onNavigate} />
          </DetailRecord>
        )}
        {detail.publishers.length > 0 && (
          <DetailRecord label="Publishers">
            <Creators values={detail.publishers} role="studio" onNavigate={onNavigate} />
          </DetailRecord>
        )}
        {detail.type && <DetailRecord label="Type">{detail.type}</DetailRecord>}
        {published && (
          <DetailRecord label="Published">
            <span className="tnum">{published}</span>
          </DetailRecord>
        )}
        {detail.totalChapters != null && (
          <DetailRecord label="Chapters">
            <span className="tnum">{detail.totalChapters}</span>
          </DetailRecord>
        )}
        {detail.finalVolume != null && (
          <DetailRecord label="Volumes">
            <span className="tnum">{detail.finalVolume}</span>
          </DetailRecord>
        )}
        {detail.contentRating && (
          <DetailRecord label="Content rating">
            <span style={{ textTransform: 'capitalize' }}>{detail.contentRating}</span>
          </DetailRecord>
        )}
      </div>
    </Paper>
  )
}
