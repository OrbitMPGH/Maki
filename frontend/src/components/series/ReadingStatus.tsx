import { useState } from 'react'
import { Button, Menu, Tooltip } from '@mantine/core'
import { useLingui } from '@lingui/react/macro'
import { errorText } from '../../api/errorText'
import { useSetReadingStatus } from '../../api/hooks'
import { useLabel } from '../../i18n-context'
import { IconCheck, IconChevronDown } from '../../lib/icons'
import { notifications } from '../../lib/toast'
import { READING_STATUSES, readingStatusVisual, statusToken, type ReadingStatus } from '../ui/status'

/** Sets the signed-in user's status on a series; the tracker push runs on the server. Shared by the series page and Home. */
export function useChangeReadingStatus() {
  const set = useSetReadingStatus()
  return (seriesId: number, status: ReadingStatus) =>
    set.mutate(
      { seriesId, status },
      { onError: (error) => notifications.show({ color: 'var(--danger)', message: errorText(error) }) },
    )
}

/** Your status on the series band, beside your rating: a menu to change it, with a tooltip saying when a rule set it. */
export function ReadingStatusMenu({
  status,
  auto,
  onChange,
}: {
  status: ReadingStatus
  auto: boolean
  onChange: (status: ReadingStatus) => void
}) {
  const { t } = useLingui()
  const label = useLabel()
  const current = readingStatusVisual(status)
  const tone = statusToken(current.color)
  const currentLabel = label(current.label)
  const [opened, setOpened] = useState(false)

  return (
    <Menu withinPortal position="bottom-start" shadow="md" opened={opened} onChange={setOpened}>
      <Menu.Target>
        <Tooltip
          label={auto ? t`Your reading status, set automatically after a while without reading` : t`Your reading status`}
          withArrow
          disabled={opened}
          events={{ hover: true, focus: true, touch: false }}
        >
          <Button
            variant="default"
            size="compact-sm"
            radius="md"
            className="hero-reading-status"
            leftSection={<current.Icon size={16} color={`var(--${tone})`} />}
            rightSection={<IconChevronDown size={14} />}
            aria-label={t`Reading status: ${currentLabel}`}
          >
            {currentLabel}
          </Button>
        </Tooltip>
      </Menu.Target>
      <Menu.Dropdown>
        {READING_STATUSES.map((value) => {
          const visual = readingStatusVisual(value)
          return (
            <Menu.Item
              key={value}
              leftSection={<visual.Icon size={16} color={`var(--${statusToken(visual.color)})`} />}
              rightSection={value === status ? <IconCheck size={14} /> : null}
              onClick={() => value !== status && onChange(value)}
            >
              {label(visual.label)}
            </Menu.Item>
          )
        })}
      </Menu.Dropdown>
    </Menu>
  )
}
