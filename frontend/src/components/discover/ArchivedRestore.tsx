import { useState } from 'react'
import { Button, type ButtonProps } from '@mantine/core'
import { IconArchiveOff } from '@tabler/icons-react'
import { Trans } from '@lingui/react/macro'
import { useRestoreSeries, useSeries } from '../../api/hooks'
import { useAuth } from '../../auth/AuthProvider'
import { isArchived } from '../../lib/series'
import { RestoreSeriesDialog } from '../series/RestoreSeriesDialog'
import { showRestoreResult } from '../series/restoreToast'

/** True when the library series with this id is archived. Null and unknown ids are not. */
export function useIsArchivedSeries(seriesId: number | null | undefined): boolean {
  const { data: library } = useSeries()
  if (seriesId == null) return false
  const series = library?.find((s) => s.id === seriesId)
  return series != null && isArchived(series)
}

/**
 * Restores an archived series straight from Discover or Add series. The series list refetches on
 * success, so the surrounding card flips to its "in library" state without any local bookkeeping.
 * A series whose files were all removed asks first whether to download them again.
 */
export function RestoreArchivedButton({
  seriesId,
  ...buttonProps
}: { seriesId: number } & Omit<ButtonProps, 'onClick' | 'loading' | 'children'>) {
  const { can } = useAuth()
  const restore = useRestoreSeries()
  const { data: library } = useSeries()
  const [asking, setAsking] = useState(false)
  if (!can('ArchiveSeries')) return null

  const series = library?.find((s) => s.id === seriesId)
  const run = (redownloadRemoved: boolean) =>
    restore.mutate(
      { id: seriesId, redownloadRemoved },
      {
        onSuccess: (result) => {
          setAsking(false)
          showRestoreResult(result, redownloadRemoved)
        },
      },
    )

  return (
    <>
      <Button
        leftSection={<IconArchiveOff size={16} />}
        loading={restore.isPending}
        onClick={() =>
          series && series.chapterFileCount === 0 && series.removedChapterCount > 0 ? setAsking(true) : run(false)
        }
        {...buttonProps}
      >
        <Trans context="series">Restore</Trans>
      </Button>
      <RestoreSeriesDialog
        opened={asking}
        onClose={() => setAsking(false)}
        seriesId={seriesId}
        canDownload={can('DownloadChapters')}
        loading={restore.isPending}
        onRestore={run}
      />
    </>
  )
}
