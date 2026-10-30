import { Checkbox, Group, Rating, Stack, Text } from '@mantine/core'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { ConfirmDialog } from '../ui/ConfirmDialog'
import { useRecycleBinDays } from '../../api/recycleBin'
import { useArchivePreview } from '../../api/hooks'
import { formatBytes } from '../../format'
import { PermanentDeleteFallback } from '../PermanentDeleteFallback'

/** Moving a series out of the library, with the one optional extra of removing its files from disk. */
export function ArchiveSeriesDialog({
  opened,
  onClose,
  title,
  seriesId = null,
  removeFiles,
  onRemoveFilesChange,
  canRemoveFiles = true,
  onConfirm,
  onConfirmPermanent,
  loading,
  leavingRating,
}: {
  opened: boolean
  onClose: () => void
  title: string
  /**
   * The one series being archived: the dialog then fetches what removing its files would free and
   * whether someone else is partway through it. Null for a bulk archive, which shows neither.
   */
  seriesId?: number | null
  removeFiles: boolean
  onRemoveFilesChange: (value: boolean) => void
  /** DeleteSeries: removing the files is a delete, so the box stays off for anyone without it. */
  canRemoveFiles?: boolean
  onConfirm: () => void
  /** Set once the server said the files cannot go to the recycle bin: offers to delete them for good. */
  onConfirmPermanent?: () => void
  loading?: boolean
  /**
   * Offered when the caller is leaving a part-read series without a rating. "Dropped at ch. 80, it's
   * a 4" is a real verdict, so the score picked here becomes the rating when the archive goes through.
   */
  leavingRating?: { value: number | null; soFar: number | null; onChange: (rating: number | null) => void }
}) {
  const { t } = useLingui()
  const binDays = useRecycleBinDays(opened && removeFiles)
  const { data: preview } = useArchivePreview(seriesId, opened)
  const sizeBytes = preview?.freeableBytes ?? 0
  const otherReaders = preview?.otherActiveReaders ?? 0
  const size = formatBytes(sizeBytes)
  const soFar = leavingRating?.soFar ?? null

  return (
    <ConfirmDialog
      opened={opened}
      onClose={onClose}
      title={t({ message: `Archive ${title}?`, context: 'series' })}
      confirmLabel={<Trans context="series">Archive</Trans>}
      onConfirm={onConfirm}
      loading={loading}
    >
      <Stack gap="md">
        <Text size="sm" c="var(--ink-3)">
          <Trans context="series">
            Maki stops checking sources for new chapters and moves it to Archived. Your reading history,
            rating and chapters stay. You can restore it any time.
          </Trans>
        </Text>
        <Checkbox
          label={sizeBytes > 0 ? t`Also remove the files to free ${size}` : t`Also remove the files`}
          checked={removeFiles && canRemoveFiles}
          disabled={!canRemoveFiles}
          onChange={(e) => onRemoveFilesChange(e.currentTarget.checked)}
        />
        {removeFiles && canRemoveFiles && (
          <Text size="sm" c="var(--ink-3)">
            <Plural
              value={binDays}
              one="The files go to the recycle bin for # day and can be restored from Settings until then."
              other="The files go to the recycle bin for # days and can be restored from Settings until then."
            />
          </Text>
        )}
        {otherReaders > 0 && (
          <Text size="sm" c="var(--warn)">
            <Plural
              value={otherReaders}
              one="# other reader is partway through this series. Archiving stops new chapters for everyone."
              other="# other readers are partway through this series. Archiving stops new chapters for everyone."
            />
          </Text>
        )}
        {leavingRating && (
          <Stack gap={4}>
            <Text size="sm" c="var(--ink-3)">
              {soFar != null ? (
                <Trans>Want to rate it before it goes? You had it at {soFar} so far.</Trans>
              ) : (
                <Trans>Want to rate it before it goes?</Trans>
              )}
            </Text>
            <Group gap="xs">
              <Rating
                count={5}
                fractions={2}
                value={leavingRating.value ? leavingRating.value / 2 : 0}
                role="group"
                aria-label={t`Your rating for ${title}`}
                onChange={(v) => leavingRating.onChange(Math.round(v * 2) || null)}
              />
              {leavingRating.value != null && (
                <Text size="sm" fw={600} c="var(--ink-2)" className="tnum">
                  {leavingRating.value}/10
                </Text>
              )}
            </Group>
          </Stack>
        )}
        {onConfirmPermanent && removeFiles && canRemoveFiles && (
          <PermanentDeleteFallback loading={loading} onConfirm={onConfirmPermanent} />
        )}
      </Stack>
    </ConfirmDialog>
  )
}
