import { useMemo } from 'react'
import { Button, Modal, Skeleton, Stack, VisuallyHidden } from '@mantine/core'
import { msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import type { MetadataField } from '../../api/types'
import { type MetadataChange, useSeriesMetadataHistory } from '../../api/seriesMetadata'
import { seriesStatusVisual } from '../ui/status'
import { CONTENT_RATING_LABELS } from '../../api/hooks'
import { SERIES_TYPE_LABELS } from '../../api/readingProfiles'
import { useLabel } from '../../i18n-context'
import { formatDateTime } from '../../format'
import { IconArrowRight } from '../../lib/icons'
import { EmptyState } from '../ui/EmptyState'
import { ModalFooter, ModalHeading } from '../ui/ModalShell'

const FIELD_LABELS: Record<MetadataField, MessageDescriptor> = {
  title: msg`Title`,
  overview: msg`Synopsis`,
  status: msg`Status`,
  totalChapters: msg`Total chapters`,
  totalVolumes: msg`Total volumes`,
  genres: msg`Genres`,
  cover: msg`Poster`,
  year: msg`Year`,
  authorStory: msg`Story`,
  authorArt: msg`Art`,
  publisher: msg`Publisher`,
  altTitles: msg`Alternative titles`,
  sortTitle: msg`Sort title`,
  type: msg`Type`,
  contentRating: msg`Content rating`,
}

interface ChangeGroup {
  key: string
  changes: MetadataChange[]
}

/** Changes saved together share a timestamp and an author, so they read as one event. */
function groupChanges(changes: MetadataChange[]): ChangeGroup[] {
  const groups: ChangeGroup[] = []
  for (const change of changes) {
    const key = `${change.changedAt}|${change.source}|${change.userName ?? ''}`
    const last = groups[groups.length - 1]
    if (last?.key === key) last.changes.push(change)
    else groups.push({ key, changes: [change] })
  }
  return groups
}

/** Metadata changes for one series, newest first: from a provider refresh or set by hand. */
export function MetadataHistoryModal({
  seriesId,
  opened,
  onClose,
}: {
  seriesId: number
  opened: boolean
  onClose: () => void
}) {
  const { t } = useLingui()
  const { data, isLoading } = useSeriesMetadataHistory(seriesId, opened)
  const groups = useMemo(() => groupChanges(data ?? []), [data])

  return (
    <Modal
      opened={opened}
      onClose={onClose}
      centered
      title={
        <ModalHeading
          title={t`Metadata history`}
          description={<Trans>Every change to this series' details, newest first.</Trans>}
        />
      }
    >
      {isLoading ? (
        <Stack gap="md" aria-hidden>
          {[0, 1, 2].map((i) => (
            <Stack key={i} gap={6}>
              <Skeleton h={12} w="40%" />
              <Skeleton h={10} w="75%" />
            </Stack>
          ))}
        </Stack>
      ) : !data || data.length === 0 ? (
        <EmptyState compact mood="asleep" title={t`No changes recorded yet.`} />
      ) : (
        <ol className="metadata-timeline">
          {groups.map((group) => (
            <ChangeEvent key={group.key} changes={group.changes} />
          ))}
        </ol>
      )}

      <ModalFooter
        status={data && data.length > 0 ? <Plural value={data.length} one="# change" other="# changes" /> : null}
      >
        <Button variant="default" onClick={onClose}>
          <Trans>Close</Trans>
        </Button>
      </ModalFooter>
    </Modal>
  )
}

function ChangeEvent({ changes }: { changes: MetadataChange[] }) {
  const { t } = useLingui()
  const first = changes[0]
  const user = first.userName
  const who =
    first.source === 'refresh' ? t`Metadata refresh` : user ? t`Set by ${user}` : t`Set by another user`

  return (
    <li className="metadata-timeline-event" data-hand={first.source !== 'refresh' || undefined}>
      <div className="metadata-timeline-head">
        <span className="metadata-timeline-who">{who}</span>
        <span className="metadata-timeline-when">{formatDateTime(first.changedAt)}</span>
      </div>
      {changes.map((change) => (
        <ChangeLine key={change.id} change={change} />
      ))}
    </li>
  )
}

function ChangeLine({ change }: { change: MetadataChange }) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const field = renderLabel(FIELD_LABELS[change.field] ?? change.field)

  const show = (value: string | null) =>
    value == null || value === ''
      ? t`none`
      : change.field === 'status'
        ? renderLabel(seriesStatusVisual(value).label)
        : change.field === 'type' && SERIES_TYPE_LABELS[value]
          ? renderLabel(SERIES_TYPE_LABELS[value])
          : change.field === 'contentRating' && CONTENT_RATING_LABELS[value]
            ? renderLabel(CONTENT_RATING_LABELS[value])
            : value

  return (
    <div className="metadata-timeline-change">
      <span className="metadata-timeline-field">{field}</span>
      {change.oldValue == null && change.newValue == null ? (
        <span className="metadata-timeline-new">
          <Trans>Changed</Trans>
        </span>
      ) : (
        <span className="metadata-timeline-value">
          <s className="metadata-timeline-old">{show(change.oldValue)}</s>{' '}
          <IconArrowRight size={12} aria-hidden className="metadata-timeline-arrow" />
          <VisuallyHidden>
            <Trans>to</Trans>
          </VisuallyHidden>{' '}
          <span className="metadata-timeline-new">{show(change.newValue)}</span>
        </span>
      )}
    </div>
  )
}
