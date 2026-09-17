import { Paper, Title } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { CONTENT_RATING_LABELS, type MangaBakaDetail } from '../../api/hooks'
import { TYPE_LABELS } from '../CatalogueFilters'
import { useLabel } from '../../i18n-context'
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
  const { t } = useLingui()
  const renderLabel = useLabel()
  const { year, status } = detail
  const ongoing = year != null && status === 'Ongoing'

  return (
    <Paper withBorder radius="lg" p="md">
      <Title order={3} fz={16}>
        <Trans>At a glance</Trans>
      </Title>

      <div className="detail-records">
        {detail.authors.length > 0 && (
          <DetailRecord label={t`Story`}>
            <Creators values={detail.authors} role="author" onNavigate={onNavigate} />
          </DetailRecord>
        )}
        {detail.artists.length > 0 && (
          <DetailRecord label={t`Art`}>
            <Creators values={detail.artists} role="artist" onNavigate={onNavigate} />
          </DetailRecord>
        )}
        {detail.publishers.length > 0 && (
          <DetailRecord label={t`Publishers`}>
            <Creators values={detail.publishers} role="studio" onNavigate={onNavigate} />
          </DetailRecord>
        )}
        {/* The wire value picks the label; an unknown one falls through as itself rather than
            as nothing, which is what `useLabel` does with a plain string. */}
        {detail.type && (
          <DetailRecord label={t`Type`}>{renderLabel(TYPE_LABELS[detail.type] ?? detail.type)}</DetailRecord>
        )}
        {year != null && (
          <DetailRecord label={t`Published`}>
            <span className="tnum">{ongoing ? <Trans>{year} – present</Trans> : year}</span>
          </DetailRecord>
        )}
        {detail.totalChapters != null && (
          <DetailRecord label={t`Chapters`}>
            <span className="tnum">{detail.totalChapters}</span>
          </DetailRecord>
        )}
        {detail.finalVolume != null && (
          <DetailRecord label={t`Volumes`}>
            <span className="tnum">{detail.finalVolume}</span>
          </DetailRecord>
        )}
        {detail.contentRating && (
          <DetailRecord label={t`Content rating`}>
            {renderLabel(CONTENT_RATING_LABELS[detail.contentRating] ?? detail.contentRating)}
          </DetailRecord>
        )}
      </div>
    </Paper>
  )
}
