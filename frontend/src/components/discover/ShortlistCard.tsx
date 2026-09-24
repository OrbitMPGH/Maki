import { Badge, Button, Text } from '@mantine/core'
import { Link } from 'react-router-dom'
import { Trans, useLingui } from '@lingui/react/macro'
import type { PlanToReadEntry } from '../../api/planToRead'

/**
 * One card in the Shortlist grid. Badge and action depend on where the title stands: still just
 * planned, requested for download, or already in the library.
 */
export function ShortlistCard({
  entry,
  onOpenDetail,
  onRemove,
  removing,
}: {
  entry: PlanToReadEntry
  onOpenDetail: () => void
  onRemove: () => void
  removing: boolean
}) {
  const { t } = useLingui()
  const inLibrary = entry.inLibrarySeriesId != null
  const requested = !inLibrary && entry.requestStatus === 'pending'

  return (
    <div className="shortlist-card">
      <button
        type="button"
        className="shortlist-cover"
        onClick={onOpenDetail}
        aria-label={t`View ${entry.title}`}
      >
        {entry.coverUrl ? (
          <img src={entry.coverUrl} alt="" loading="lazy" decoding="async" />
        ) : (
          <div className="shortlist-placeholder">{entry.title}</div>
        )}
        <Badge
          className="shortlist-badge"
          size="sm"
          variant="filled"
          color={inLibrary ? 'var(--ok)' : requested ? 'var(--warn)' : 'var(--info)'}
        >
          {inLibrary ? <Trans>In library</Trans> : requested ? <Trans>Requested</Trans> : <Trans>Planned</Trans>}
        </Badge>
      </button>

      <div className="shortlist-body">
        <button type="button" className="shortlist-title-btn" onClick={onOpenDetail}>
          <Text fw={600} size="sm" lineClamp={2}>
            {entry.title}
          </Text>
        </button>

        <div className="shortlist-actions">
          {inLibrary ? (
            <Button
              component={Link}
              to={`/series/${entry.inLibrarySeriesId}`}
              variant="default"
              size="sm"
            >
              <Trans>View in library</Trans>
            </Button>
          ) : (
            <Button variant="default" size="sm" onClick={onOpenDetail}>
              <Trans>Add to library</Trans>
            </Button>
          )}
          <Button
            variant="subtle"
            color="var(--danger)"
            size="sm"
            loading={removing}
            onClick={onRemove}
          >
            <Trans>Remove</Trans>
          </Button>
        </div>
      </div>
    </div>
  )
}
