import { ActionIcon, Group, Popover, Stack, Text, Tooltip } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { IconHistory } from '@tabler/icons-react'
import { useRatingHistory, type RatingHistoryEntry } from '../../api/hooks'
import { formatDate, formatDecimal } from '../../format'

function EntryLabel({ entry }: { entry: RatingHistoryEntry }) {
  const { rating } = entry
  const chapter = entry.atChapter != null ? formatDecimal(entry.atChapter) : null
  if (entry.final) {
    return chapter != null ? <Trans>{rating}/10, your rating at ch. {chapter}</Trans> : <Trans>{rating}/10, your rating</Trans>
  }
  return chapter != null ? <Trans>{rating}/10 so far, at ch. {chapter}</Trans> : <Trans>{rating}/10 so far</Trans>
}

/** How the reader's score on a series moved as they read. Draws nothing until there are two entries. */
export function RatingHistoryButton({ seriesId }: { seriesId: number }) {
  const { t } = useLingui()
  const { data: history } = useRatingHistory(seriesId)
  if (!history || history.length < 2) return null

  return (
    <Popover position="bottom-start" withArrow shadow="md">
      <Popover.Target>
        <Tooltip label={t`Your rating over time`} withArrow>
          <ActionIcon size="sm" variant="subtle" color="var(--neutral)" aria-label={t`Your rating over time`}>
            <IconHistory size={14} />
          </ActionIcon>
        </Tooltip>
      </Popover.Target>
      <Popover.Dropdown>
        <Stack gap={6}>
          <Text size="xs" fw={600} c="var(--ink-3)">
            <Trans>Your rating over time</Trans>
          </Text>
          {history.map((entry, i) => (
            <Group key={i} gap="md" justify="space-between" wrap="nowrap">
              <Text size="sm" className="tnum">
                <EntryLabel entry={entry} />
              </Text>
              <Text size="xs" c="var(--ink-4)">
                {formatDate(entry.createdAt)}
              </Text>
            </Group>
          ))}
        </Stack>
      </Popover.Dropdown>
    </Popover>
  )
}
