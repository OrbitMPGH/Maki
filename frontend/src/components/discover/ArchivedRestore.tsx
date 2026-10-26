import { Button, type ButtonProps } from '@mantine/core'
import { notifications } from '@mantine/notifications'
import { IconArchiveOff } from '@tabler/icons-react'
import { t as staticT } from '@lingui/core/macro'
import { Trans } from '@lingui/react/macro'
import { useRestoreSeries, useSeries } from '../../api/hooks'
import { useAuth } from '../../auth/AuthProvider'
import { isArchived } from '../../lib/series'

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
 */
export function RestoreArchivedButton({
  seriesId,
  ...buttonProps
}: { seriesId: number } & Omit<ButtonProps, 'onClick' | 'loading' | 'children'>) {
  const { can } = useAuth()
  const restore = useRestoreSeries()
  if (!can('ArchiveSeries')) return null
  return (
    <Button
      leftSection={<IconArchiveOff size={16} />}
      loading={restore.isPending}
      onClick={() =>
        restore.mutate(
          { id: seriesId },
          {
            onSuccess: () =>
              notifications.show({
                message: staticT({ message: 'Restored to the library', context: 'series' }),
                color: 'var(--ok)',
              }),
          },
        )
      }
      {...buttonProps}
    >
      <Trans context="series">Restore</Trans>
    </Button>
  )
}
