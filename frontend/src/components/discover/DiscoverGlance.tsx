import { Trans, useLingui } from '@lingui/react/macro'
import { IconInfoCircle } from '../../lib/icons'
import { altTitleLabel, readableTitles } from '../../api/titles'
import { CONTENT_RATING_LABELS, type MangaBakaDetail } from '../../api/hooks'
import { TYPE_LABELS } from '../CatalogueFilters'
import { useLabel } from '../../i18n-context'
import { Creators, DetailRecord } from './DiscoverRecord'
import { Panel } from '../ui/Panel'
import { PanelTitle } from '../ui/PanelTitle'

/**
 * The facts about a series that are read rather than scanned: who made it, who prints it, and the
 * publication numbers the band doesn't carry.
 */
export function DiscoverGlance({
  detail,
  onNavigate,
}: {
  detail: MangaBakaDetail
  /** Closes the modal before a creator link navigates out from under it. */
  onNavigate: () => void
}) {
  const { t, i18n } = useLingui()
  const renderLabel = useLabel()
  const { year, status } = detail
  const altTitles = readableTitles(detail.altTitles, i18n.locale)
  const ongoing = year != null && status === 'Ongoing'

  return (
    <Panel>
      <PanelTitle order={2} icon={IconInfoCircle}>
        <Trans>At a glance</Trans>
      </PanelTitle>

      <dl className="detail-records">
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
        {/* The band keeps one line for the romanized and native titles; the rest live here. */}
        {altTitles.length > 0 && (
          <DetailRecord label={t`Also known as`}>
            {altTitles.map((a, i) => (
              <span key={i}>
                {i > 0 && ', '}
                <span lang={a.language ?? undefined}>{a.title}</span>
                {altTitleLabel(a).slice(a.title.length)}
              </span>
            ))}
          </DetailRecord>
        )}
        {detail.contentRating && (
          <DetailRecord label={t`Content rating`}>
            {renderLabel(CONTENT_RATING_LABELS[detail.contentRating] ?? detail.contentRating)}
          </DetailRecord>
        )}
      </dl>
    </Panel>
  )
}
