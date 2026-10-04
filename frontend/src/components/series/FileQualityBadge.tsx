import { Badge, Stack, Text, Tooltip } from '@mantine/core'
import { IconLock } from '@tabler/icons-react'
import { msg } from '@lingui/core/macro'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import type { MessageDescriptor } from '@lingui/core'
import type { ChapterFileQualityDto } from '../../api/types'
import { QUALITY_TIER_COLOR, QUALITY_TIER_LABELS } from '../../api/upgrades'
import { useLabel } from '../../i18n-context'

const TIER_COLOR = QUALITY_TIER_COLOR

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

  const { tier, group, pageCount, medianWidth, imageFormat, measured, score, cutoffMet, trusted } = quality
  const tierLabel = renderLabel(QUALITY_TIER_LABELS[tier])
  const tierUnknown = tier === 'unknown'
  // Subtle by design: this badge is a quiet quality signal already, so the cutoff-unmet marker is a
  // small dot rather than a colour swap that would make it compete with the tier badge next to it.
  const cutoffUnmet = cutoffMet === false
  // Normally an unknown tier with no width renders nothing at all (see the doc comment above), but a
  // cutoff-unmet or trusted file still has to show *something*: an Activity row naming a file that
  // falls short of its profile, or a protected file with an unknown tier, can't have a blank cell,
  // so fall back to the bare tier label.
  if (tierUnknown && medianWidth == null && !cutoffUnmet && !trusted) return null
  const label = tierUnknown
    ? medianWidth != null
      ? t`${medianWidth}px`
      : tierLabel
    : medianWidth != null
      ? t`${tierLabel} · ${medianWidth}px`
      : tierLabel
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
          {score != null && (
            <Text size="xs">
              <Trans>Score: {score}</Trans>
            </Text>
          )}
          {trusted && (
            <Text size="xs">
              <Trans>Protected from upgrades</Trans>
            </Text>
          )}
          {cutoffUnmet && (
            <Text size="xs" c="var(--warn)">
              <Trans>Below the series' upgrade cutoff</Trans>
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
      <Badge
        size="sm"
        variant="light"
        color={TIER_COLOR[tier]}
        rightSection={
          trusted ? (
            <IconLock size={9} aria-hidden />
          ) : cutoffUnmet ? (
            <span
              aria-hidden
              style={{
                display: 'inline-block',
                width: 5,
                height: 5,
                borderRadius: '50%',
                background: 'var(--warn)',
              }}
            />
          ) : undefined
        }
      >
        {label}
      </Badge>
    </Tooltip>
  )
}
