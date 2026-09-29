import { Badge, Button, Group, Paper, Stack, Text, Tooltip } from '@mantine/core'
import { notifications } from '@mantine/notifications'
import { IconDownload, IconX } from '@tabler/icons-react'
import { Link } from 'react-router-dom'
import { Trans, useLingui } from '@lingui/react/macro'
import { useQueue } from '../../api/hooks'
import {
  QUALITY_TIER_COLOR,
  QUALITY_TIER_LABELS,
  proposalCountsText,
  proposalReasonLabel,
  releaseSpanText,
  useDismissProposal,
  useGrabProposal,
} from '../../api/upgrades'
import type { TorrentProposalDto } from '../../api/upgrades'
import { useAuth } from '../../auth/AuthProvider'
import { useLabel } from '../../i18n-context'
import { formatBytes } from '../../format'
import { Panel } from '../ui/Panel'

/**
 * A torrent volume release the nightly search found but did not grab on its own, waiting for a
 * decision. `banner` is the slim form the series page puts beside the unlinked-files hint; the
 * default is the panel the Activity Upgrades tab lists, which also links to the series.
 */
export function TorrentProposalCard({
  proposal,
  banner = false,
}: {
  proposal: TorrentProposalDto
  banner?: boolean
}) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const { can } = useAuth()
  const canDecide = can('DownloadChapters')
  const { data: queue } = useQueue()
  const grab = useGrabProposal()
  const dismiss = useDismissProposal()

  const { id, seriesId, seriesTitle, title, indexer, sizeBytes, span, reasons, tier, queueItemId } = proposal
  const spanText = releaseSpanText(span)
  const countsText = proposalCountsText(proposal)
  const tierLabel = renderLabel(QUALITY_TIER_LABELS[tier])
  const size = formatBytes(sizeBytes)
  const running =
    queueItemId != null ||
    (queue?.items ?? []).some((q) => q.seriesId === seriesId && q.releaseTitle === title)

  const onGrab = () =>
    grab.mutate(
      { id, seriesId },
      { onSuccess: () => notifications.show({ message: t`Sent to qBittorrent: ${title}`, color: 'var(--ok)' }) },
    )
  const onDismiss = () =>
    dismiss.mutate(
      { id, seriesId },
      { onSuccess: () => notifications.show({ message: t`Proposal dismissed`, color: 'var(--ok)' }) },
    )

  const body = (
    <Group justify="space-between" align="flex-start" wrap="wrap" gap="sm">
      <Stack gap={6} style={{ flex: 1, minWidth: 240 }}>
        {!banner && (
          <Text component={Link} to={`/series/${seriesId}?tab=chapters`} size="sm" fw={600} c="brand.4" lineClamp={1}>
            {seriesTitle}
          </Text>
        )}
        <Text size="sm" fw={banner ? 600 : 400} lineClamp={2} style={{ wordBreak: 'break-word' }}>
          {title}
        </Text>
        <Group gap={6} wrap="wrap">
          <Badge size="sm" variant="light" color={QUALITY_TIER_COLOR[tier]}>
            {tierLabel}
          </Badge>
          <Badge size="sm" variant="light" color="var(--neutral)">
            {indexer}
          </Badge>
          <Text size="xs" c="var(--ink-3)" className="tnum">
            {size}
          </Text>
          {spanText && (
            <Text size="xs" c="var(--ink-3)">
              {spanText}
            </Text>
          )}
        </Group>
        <Text size="sm" c="var(--ink-2)">
          {countsText}
        </Text>
        {reasons.length > 0 && (
          <Group gap={6} wrap="wrap">
            {reasons.map((code) => (
              <Badge key={code} size="xs" variant="light" color="var(--warn)" style={{ textTransform: 'none' }}>
                {proposalReasonLabel(renderLabel, code)}
              </Badge>
            ))}
          </Group>
        )}
      </Stack>
      {canDecide && (
        <Group gap="xs" wrap="nowrap">
          <Tooltip label={t`Already downloading`} withArrow disabled={!running}>
            <span>
              <Button
                size="xs"
                variant="light"
                leftSection={<IconDownload size={14} />}
                loading={grab.isPending}
                disabled={running || dismiss.isPending}
                onClick={onGrab}
              >
                <Trans>Grab</Trans>
              </Button>
            </span>
          </Tooltip>
          <Button
            size="xs"
            variant="subtle"
            color="var(--neutral)"
            leftSection={<IconX size={14} />}
            loading={dismiss.isPending}
            disabled={grab.isPending}
            onClick={onDismiss}
          >
            <Trans>Dismiss</Trans>
          </Button>
        </Group>
      )}
    </Group>
  )

  if (banner) {
    return (
      <Paper className="series-detail-chapter-hint" withBorder p="xs" radius="lg">
        {body}
      </Paper>
    )
  }
  return (
    <Panel p="md" edge="warn" edgeSide="left">
      {body}
    </Panel>
  )
}
