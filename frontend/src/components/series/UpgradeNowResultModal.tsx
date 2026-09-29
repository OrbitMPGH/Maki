import { Group, Modal, Stack, Text } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { plural, t as now } from '@lingui/core/macro'
import { upgradeReasonLabel } from '../../api/upgrades'
import type { UpgradeCandidateOutcomeDto, UpgradeScanResultDto } from '../../api/upgrades'
import { useLabel } from '../../i18n-context'
import { useSourceLabel } from '../../sourceLabels'

/**
 * Skip codes a chapter scan can record for the chapter itself rather than any one candidate, which
 * is why `candidates` comes back empty even though the chapter had sources to compare against.
 */
const CHAPTER_LEVEL_SKIPS = [
  'queued',
  'trusted',
  'cutoff_met',
  'no_profile',
  'upgrades_disabled',
  'incognito',
  'unmeasured',
  'shared_file',
  'unsupported_file',
] as const

/** `<width>px, <n> pages, score <s>`, only the parts a probe actually measured. */
function measurementText(candidate: UpgradeCandidateOutcomeDto): string | null {
  const { medianWidth, pageCount, score } = candidate
  const parts: string[] = []
  if (medianWidth != null) parts.push(now`${medianWidth}px`)
  if (pageCount != null) parts.push(plural(pageCount, { one: '# page', other: '# pages' }))
  if (score != null) parts.push(now`score ${score}`)
  return parts.length > 0 ? parts.join(', ') : null
}

/**
 * Result of running "Upgrade now" on a single chapter: what won (if anything) and, underneath it,
 * every candidate the scan actually looked at and why it did or didn't take that one.
 */
export function UpgradeNowResultModal({
  opened,
  onClose,
  result,
}: {
  opened: boolean
  onClose: () => void
  result: UpgradeScanResultDto | null
}) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const sourceLabel = useSourceLabel()

  const winner =
    result?.candidates.find((c) => c.mappingId === result.queuedFromMappingId && result.queuedFromMappingId != null) ??
    null
  const winnerSource = winner ? sourceLabel(winner.sourceName) : null
  const title = winnerSource ? now`Queued upgrade from ${winnerSource}` : t`No better copy found`
  const winnerMeasurement = winner ? measurementText(winner) : null

  const chapterSkip =
    result && result.candidates.length === 0
      ? CHAPTER_LEVEL_SKIPS.find((code) => (result.skipped[code] ?? 0) > 0)
      : undefined

  return (
    <Modal opened={opened} onClose={onClose} title={title} size="sm" centered>
      {result && (
        <Stack gap="sm">
          {winnerMeasurement && (
            <Text size="sm" c="var(--ink-3)">
              {winnerMeasurement}
            </Text>
          )}
          {result.candidates.length > 0 ? (
            <Stack gap={6}>
              {result.candidates.map((candidate) => {
                const reasonText = upgradeReasonLabel(renderLabel, candidate.reason)
                const measurement = candidate.probed ? measurementText(candidate) : null
                return (
                  <Group
                    key={`${candidate.mappingId}-${candidate.sourceChapterId}`}
                    justify="space-between"
                    wrap="nowrap"
                    align="flex-start"
                    gap="sm"
                  >
                    <Text size="sm" truncate>
                      {sourceLabel(candidate.sourceName)}
                    </Text>
                    <Text size="xs" c="var(--ink-3)" ta="right">
                      {measurement ? now`${reasonText} - ${measurement}` : reasonText}
                    </Text>
                  </Group>
                )
              })}
            </Stack>
          ) : chapterSkip ? (
            <Text size="sm" c="var(--ink-3)">
              {upgradeReasonLabel(renderLabel, chapterSkip)}
            </Text>
          ) : (
            <Text size="sm" c="var(--ink-3)">
              <Trans>No sources to compare against.</Trans>
            </Text>
          )}
        </Stack>
      )}
    </Modal>
  )
}
