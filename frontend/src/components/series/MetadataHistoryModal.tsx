import { Loader, Modal, Stack, Text } from '@mantine/core'
import { msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { Trans, useLingui } from '@lingui/react/macro'
import type { MetadataField } from '../../api/types'
import { type MetadataChange, useSeriesMetadataHistory } from '../../api/seriesMetadata'
import { seriesStatusVisual } from '../ui/status'
import { useLabel } from '../../i18n-context'
import { formatDateTime } from '../../format'

const FIELD_LABELS: Record<MetadataField, MessageDescriptor> = {
  title: msg`Title`,
  overview: msg`Synopsis`,
  status: msg`Status`,
  totalChapters: msg`Total chapters`,
  totalVolumes: msg`Total volumes`,
  genres: msg`Genres`,
  cover: msg`Poster`,
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

  return (
    <Modal opened={opened} onClose={onClose} title={t`Metadata history`} centered>
      {isLoading ? (
        <Loader size="sm" />
      ) : !data || data.length === 0 ? (
        <Text size="sm" c="var(--ink-3)">
          <Trans>No changes recorded yet.</Trans>
        </Text>
      ) : (
        <div className="series-records">
          {data.map((change) => (
            <ChangeRow key={change.id} change={change} />
          ))}
        </div>
      )}
    </Modal>
  )
}

function ChangeRow({ change }: { change: MetadataChange }) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const field = renderLabel(FIELD_LABELS[change.field] ?? change.field)
  const when = formatDateTime(change.changedAt)
  const user = change.userName
  const who =
    change.source === 'refresh' ? t`Metadata refresh` : user ? t`Set by ${user}` : t`Set by hand`

  const show = (value: string | null) =>
    value == null || value === ''
      ? t`none`
      : change.field === 'status'
        ? renderLabel(seriesStatusVisual(value).label)
        : value
  const from = show(change.oldValue)
  const to = show(change.newValue)

  return (
    <div className="series-record">
      <Text size="xs" c="var(--ink-4)">
        {when} · {who}
      </Text>
      <Stack gap={0}>
        <Text size="sm" fw={500} c="var(--ink-2)">
          {field}
        </Text>
        <Text size="sm" c="var(--ink-3)" style={{ overflowWrap: 'anywhere' }}>
          {change.oldValue == null && change.newValue == null ? (
            <Trans>Changed</Trans>
          ) : (
            <Trans>
              {from} to {to}
            </Trans>
          )}
        </Text>
      </Stack>
    </div>
  )
}
