import { Badge, Group, Stack, Text, Title, Tooltip } from '@mantine/core'
import { IconCalendarEvent } from '@tabler/icons-react'
import { Trans, useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'
import { confidenceLevel, useSeriesOutlook, type ReleaseOutlookDto } from '../../api/releases'
import { formatDayRange, formatWeekdayDateTime } from '../../format'
import { useLabel } from '../../i18n-context'
import { relativeTime } from '../ui/time'
import { CONFIDENCE_LABEL, REFRESH_TIER_TEXT } from './outlookText'

/** The series page's "Next chapter" block. Renders nothing until the outlook has loaded. */
export function ReleaseOutlookSummary({ seriesId }: { seriesId: number }) {
  const { data } = useSeriesOutlook(seriesId)
  return data ? <ReleaseOutlookView outlook={data} /> : null
}

export function ReleaseOutlookView({ outlook }: { outlook: ReleaseOutlookDto }) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const { state, windowStart, windowEnd, announcedAt, announcedBy, hits, checked, lastRelease, nextCheckAt } = outlook
  const days = outlook.interval == null ? null : Math.max(1, Math.round(outlook.interval))
  const usually = days == null ? null : plural(days, { one: 'Usually every day.', other: 'Usually every # days.' })
  const range = windowStart && windowEnd ? formatDayRange(windowStart, windowEnd) : null
  const last = lastRelease ? relativeTime(lastRelease) : null
  const level = confidenceLevel(outlook.confidence)
  const showsWindow = (state === 'Expected' || state === 'Late') && range
  const announced = announcedAt ? formatWeekdayDateTime(announcedAt) : null
  const nextDue = nextCheckAt != null && new Date(nextCheckAt).getTime() <= Date.now()
  const next = nextCheckAt && !nextDue ? relativeTime(nextCheckAt) : null

  return (
    <Stack gap={6} className="release-outlook">
      <Group justify="space-between" align="baseline" wrap="nowrap">
        <Title order={3} size="h4" fz="var(--type-body)">
          <Trans>Next chapter</Trans>
        </Title>
        {showsWindow && (
          <Tooltip
            withArrow
            multiline
            w={260}
            label={t`${hits} of the last ${checked} chapters came out inside the window drawn for them.`}
          >
            <Badge variant="light" className="release-confidence" data-level={level}>
              {renderLabel(CONFIDENCE_LABEL[level])}
            </Badge>
          </Tooltip>
        )}
      </Group>

      {announced && announcedBy && (
        <Group gap={6} wrap="nowrap" className="release-announced">
          <IconCalendarEvent size={16} aria-hidden />
          <Text size="sm" fw={600}>
            <Trans>
              {announcedBy} has it scheduled for {announced}
            </Trans>
          </Text>
        </Group>
      )}

      <Text size="sm">
        {state === 'Expected' && range ? (
          <Trans>Likely {range}</Trans>
        ) : state === 'Late' && range ? (
          <Trans>Running late. It was likely {range}.</Trans>
        ) : state === 'Stalled' ? (
          <Trans>Quiet for longer than its usual rhythm, so there is no estimate.</Trans>
        ) : state === 'Irregular' ? (
          <Trans>Releases too irregularly to estimate.</Trans>
        ) : (
          <Trans>Not enough release history for an estimate yet.</Trans>
        )}
      </Text>

      {(usually || last) && (
        <Text size="xs" c="var(--ink-3)">
          {usually}
          {usually && last && ' '}
          {last && <Trans>Last chapter {last}.</Trans>}
        </Text>
      )}

      <Text size="xs" c="var(--ink-3)">
        {outlook.refreshing ? (
          <>
            {renderLabel(REFRESH_TIER_TEXT[outlook.refreshTier])}
            {' '}
            {nextDue ? <Trans>The next check is due.</Trans> : next && <Trans>Next check {next}.</Trans>}
          </>
        ) : (
          <Trans>Not checked for new chapters: it is finished and complete, or has no source switched on.</Trans>
        )}
      </Text>
    </Stack>
  )
}
