import { Button, Modal, Stack, Text } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { Link } from 'react-router-dom'
import { ModalHeading, ModalNote } from '../ui/ModalShell'
import { EmptyState } from '../ui/EmptyState'
import { errorText } from '../../api/errorText'
import { formatDateTime } from '../../format'
import { useDeclinedReleases, useUnblockRelease } from '../../api/torrentLifecycle'
import { MODAL_SIZE } from '../ui/modalSizes'

/**
 * Releases the automatic search passes over: proposals someone dismissed, and torrents that stalled,
 * vanished from the client or were removed from the queue. Allowing one again lets the next search take it.
 */
export function DeclinedReleasesModal({ opened, onClose }: { opened: boolean; onClose: () => void }) {
  const { t } = useLingui()
  const declined = useDeclinedReleases(opened)
  const unblock = useUnblockRelease()
  const rows = declined.data ?? []

  return (
    <Modal
      opened={opened}
      onClose={onClose}
      size={MODAL_SIZE.wide}
      title={
        <ModalHeading
          title={<Trans>Declined releases</Trans>}
          description={<Trans>The automatic search skips these. Allow one again to let the next search take it.</Trans>}
        />
      }
      closeButtonProps={{ 'aria-label': t`Close` }}
    >
      <Stack gap="sm">
        {declined.error && <ModalNote tone="danger">{errorText(declined.error)}</ModalNote>}
        {unblock.error && <ModalNote tone="danger">{errorText(unblock.error)}</ModalNote>}
        {declined.isSuccess && rows.length === 0 && (
          <EmptyState
            compact
            mood="pleased"
            title={t`No declined releases`}
            description={t`Releases you dismiss, and torrents that stall or disappear, are listed here.`}
          />
        )}
        {rows.length > 0 && (
          <div className="hair-table-wrap">
            <table className="hair-table">
              <thead>
                <tr>
                  <th><Trans>Release</Trans></th>
                  <th><Trans>Series</Trans></th>
                  <th><Trans>Declined</Trans></th>
                  <th aria-label={t`Actions`} />
                </tr>
              </thead>
              <tbody>
                {rows.map((row) => (
                  <tr key={row.id}>
                    <td>
                      <Text size="sm" style={{ overflowWrap: 'anywhere' }}>{row.title}</Text>
                      <Text size="xs" c="var(--ink-3)">{row.indexer}</Text>
                    </td>
                    <td>
                      <Text className="series-title" component={Link} to={`/series/${row.seriesId}`} onClick={onClose}>
                        {row.seriesTitle}
                      </Text>
                    </td>
                    <td>
                      <Text size="sm" c="var(--ink-2)" style={{ whiteSpace: 'nowrap' }}>
                        {formatDateTime(row.resolvedAtUtc ?? row.createdAtUtc)}
                      </Text>
                    </td>
                    <td style={{ textAlign: 'end' }}>
                      <Button
                        size="xs"
                        variant="default"
                        loading={unblock.isPending && unblock.variables === row.id}
                        onClick={() => unblock.mutate(row.id)}
                        aria-label={t`Allow ${row.title} again`}
                      >
                        <Trans>Allow again</Trans>
                      </Button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Stack>
    </Modal>
  )
}
