import { Badge, Stack, Text, Tooltip } from '@mantine/core'
import { msg } from '@lingui/core/macro'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import type { MessageDescriptor } from '@lingui/core'
import type { ChapterFileQualityDto } from '../../api/types'
import { useLabel } from '../../i18n-context'

const TIER_COLOR: Record<ChapterFileQualityDto['tier'], string> = {
  unknown: 'gray',
  aggregator: 'orange',
  scanlator: 'blue',
  official: 'teal',
  volume: 'indigo',
}

const TIER_LABEL: Record<ChapterFileQualityDto['tier'], MessageDescriptor> = {
  unknown: msg`Unknown`,
  aggregator: msg`Aggregator`,
  scanlator: msg`Scanlator`,
  official: msg`Official`,
  volume: msg`Volume`,
}

/** `imageFormat` is mostly raw codec names (jpg, webp, ...), which are data, not copy. Only the
 * two words the backend itself chooses need translating. */
const FORMAT_LABEL: Partial<Record<string, MessageDescriptor>> = {
  mixed: msg`Mixed`,
  unknown: msg`Unknown`,
}

/**
 * Quiet quality signal that sits next to the Source badge: which kind of release a chapter's file
 * is (a scanlator's own work, an aggregator repost, an official release, a volume/compilation
 * archive), and once the backfill has opened the archive, its resolution.
 *
 * Renders nothing for a chapter with no file, and nothing for a file the backfill hasn't reached
 * yet and couldn't otherwise classify from its name alone, so an "Unknown" badge on every unmeasured
 * chapter would just be noise next to the one badge that means something. Once a file is measured
 * but its tier is still unknown, the label drops the tier word and shows only the width; if there
 * is no width either, it renders nothing.
 */
export function FileQualityBadge({
  quality,
}: {
  quality: ChapterFileQualityDto | null | undefined
}) {
  const renderLabel = useLabel()
  const { t } = useLingui()
  if (!quality || (quality.tier === 'unknown' && !quality.measured)) return null

  const { tier, group, pageCount, medianWidth, imageFormat, measured } = quality
  const tierLabel = renderLabel(TIER_LABEL[tier])
  const tierUnknown = tier === 'unknown'
  if (tierUnknown && medianWidth == null) return null
  const label = tierUnknown ? t`${medianWidth}px` : medianWidth != null ? t`${tierLabel} · ${medianWidth}px` : tierLabel
  const formatLabel = imageFormat ? renderLabel(FORMAT_LABEL[imageFormat] ?? imageFormat.toUpperCase()) : null

  return (
    <Tooltip
      withArrow
      multiline
      label={
        <Stack gap={2}>
          {group && (
            <Text size="xs" fw={600}>
              {group}
            </Text>
          )}
          {pageCount != null && (
            <Text size="xs">
              <Plural value={pageCount} one="# page" other="# pages" />
            </Text>
          )}
          {formatLabel && (
            <Text size="xs">
              <Trans>Format: {formatLabel}</Trans>
            </Text>
          )}
          {!measured && (
            <Text size="xs" c="var(--ink-3)">
              <Trans>Not measured yet</Trans>
            </Text>
          )}
        </Stack>
      }
    >
      <Badge size="sm" variant="light" color={TIER_COLOR[tier]}>
        {label}
      </Badge>
    </Tooltip>
  )
}
