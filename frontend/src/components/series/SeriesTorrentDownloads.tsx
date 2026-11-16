import { useState } from 'react'
import { Button, Group, Progress, Stack, Text } from '@mantine/core'
import { Link } from 'react-router-dom'
import { Trans, useLingui } from '@lingui/react/macro'
import type { QueueItemDto } from '../../api/types'
import { queueErrorMessage } from '../../api/queue'
import { useLabel } from '../../i18n-context'
import { IconDownload } from '../../lib/icons'
import { ImportReviewModal } from '../ImportReviewModal'
import { Callout, type CalloutTone } from '../ui/Callout'
import { StatusDot } from '../ui/StatusDot'
import { isQueueActive, needsImportReview, queueStatusVisual, statusToken } from '../ui/status'

/**
 * The series' torrent grabs, which have no chapter row to show their state on. One slim banner
 * per release, tinted by status, from grab through import.
 */
export function SeriesTorrentDownloads({
  items,
  canReview,
}: {
  items: QueueItemDto[]
  canReview: boolean
}) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const [reviewing, setReviewing] = useState<number | null>(null)

  if (items.length === 0) return null

  return (
    <Stack gap="xs" mt="lg">
      {items.map((q) => {
        const visual = queueStatusVisual(q.status)
        const toneStem = statusToken(visual.color)
        const failure = queueErrorMessage(q, renderLabel)
        const percent = q.pagesTotal > 0 ? Math.round((q.pagesDone / q.pagesTotal) * 100) : null
        const showProgress = q.status === 'Downloading' && percent != null
        return (
          <Callout
            key={q.id}
            tone={toneStem as CalloutTone}
            icon={IconDownload}
            actions={
              <>
                {needsImportReview(q.status) && canReview && (
                  <Button size="xs" variant="light" color="var(--warn)" onClick={() => setReviewing(q.id)}>
                    <Trans>Review</Trans>
                  </Button>
                )}
                <Button size="xs" variant="subtle" color="var(--neutral)" component={Link} to="/activity">
                  <Trans>Open Activity</Trans>
                </Button>
              </>
            }
          >
                <Stack gap={4} style={{ minWidth: 0 }}>
                  <Text size="sm" fw={600} lineClamp={2} style={{ wordBreak: 'break-word' }}>
                    {q.releaseTitle}
                  </Text>
                  <Group gap="xs" wrap="nowrap">
                    <StatusDot tone={toneStem} live={isQueueActive(q.status)}>
                      {renderLabel(visual.label)}
                    </StatusDot>
                    {showProgress && (
                      <>
                        <Progress
                          value={percent}
                          animated
                          style={{ flex: 1, maxWidth: 240 }}
                          aria-label={t`Download progress`}
                        />
                        <Text size="xs" c="var(--ink-3)" className="tnum">
                          {percent}%
                        </Text>
                      </>
                    )}
                  </Group>
                  {failure && (
                    <Text size="xs" c="var(--ink-3)" lineClamp={2}>
                      {failure}
                    </Text>
                  )}
                </Stack>
          </Callout>
        )
      })}
      <ImportReviewModal queueItemId={reviewing} onClose={() => setReviewing(null)} />
    </Stack>
  )
}
