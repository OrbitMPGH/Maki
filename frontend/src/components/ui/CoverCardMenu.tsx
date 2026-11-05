import { useRef, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { Menu } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { IconArrowRight, IconBell, IconBellOff, IconBook, IconDots, IconEye, IconEyeOff } from '../../lib/icons'
import type { SeriesDto } from '../../api/types'
import { useLibrarySettings, useSetMonitorMode, useSetSeriesNotificationMode } from '../../api/hooks'
import { useContinueReading } from '../../api/reader'
import { useAuth } from '../../auth/AuthProvider'

/**
 * The ⋯ button on a library cover. A sibling of the card's link, like Home's `ReadingCardMenu`, but
 * the Mantine menu only mounts while open: the grid draws hundreds of these, and a Menu per card at
 * rest costs a floating-ui instance each.
 */
export function CoverCardMenu({ series }: { series: SeriesDto }) {
  const { t } = useLingui()
  const [open, setOpen] = useState(false)
  const buttonRef = useRef<HTMLButtonElement>(null)
  // The menu closes on the press that lands on this button, so the click after it must not reopen it.
  const closedByPress = useRef(false)
  const seriesTitle = series.displayTitle

  const close = () => {
    setOpen(false)
    setTimeout(() => {
      const active = document.activeElement
      if (!active || active === document.body) buttonRef.current?.focus()
    })
  }

  return (
    <>
      <button
        ref={buttonRef}
        type="button"
        className="reading-card-menu cover-card-menu"
        aria-label={t`More options for ${seriesTitle}`}
        aria-haspopup="menu"
        aria-expanded={open}
        data-expanded={open || undefined}
        onPointerDown={() => {
          closedByPress.current = open
        }}
        onClick={() => {
          if (closedByPress.current) {
            closedByPress.current = false
            return
          }
          setOpen(true)
        }}
      >
        <IconDots size={16} />
      </button>
      {open && <CoverCardMenuDropdown series={series} onClose={close} />}
    </>
  )
}

function CoverCardMenuDropdown({ series, onClose }: { series: SeriesDto; onClose: () => void }) {
  const navigate = useNavigate()
  const { can } = useAuth()
  const { data: continueAt } = useContinueReading(series.id)
  const { data: librarySettings } = useLibrarySettings()
  const setMonitorMode = useSetMonitorMode()
  const setNotificationMode = useSetSeriesNotificationMode()
  const muted = series.notificationMode === 'Muted'
  const seriesId = series.id
  // Turning monitoring back on uses the mode a newly added series gets.
  const defaultMode = librarySettings?.newSeriesMonitorMode
  const monitorMode = defaultMode && defaultMode !== 'None' ? defaultMode : 'All'

  return (
    <Menu opened onChange={(opened) => !opened && onClose()} withinPortal position="bottom-start" shadow="md">
      <Menu.Target>
        <span className="cover-card-menu-anchor" aria-hidden />
      </Menu.Target>
      <Menu.Dropdown>
        <Menu.Item leftSection={<IconArrowRight size={16} />} onClick={() => navigate(`/series/${seriesId}`)}>
          <Trans>Open series</Trans>
        </Menu.Item>
        {continueAt && (
          <Menu.Item leftSection={<IconBook size={16} />} onClick={() => navigate(`/read/${continueAt.chapterId}`)}>
            <Trans>Continue reading</Trans>
          </Menu.Item>
        )}
        <Menu.Divider />
        {can('EditMetadata') && (
          <Menu.Item
            leftSection={series.monitored ? <IconEyeOff size={16} /> : <IconEye size={16} />}
            onClick={() => setMonitorMode.mutate({ seriesId, mode: series.monitored ? 'None' : monitorMode })}
          >
            {series.monitored ? <Trans>Stop monitoring</Trans> : <Trans>Monitor for new chapters</Trans>}
          </Menu.Item>
        )}
        <Menu.Item
          leftSection={muted ? <IconBell size={16} /> : <IconBellOff size={16} />}
          onClick={() => setNotificationMode.mutate({ seriesId, mode: muted ? 'Default' : 'Muted' })}
        >
          {muted ? <Trans>Unmute notifications</Trans> : <Trans>Mute notifications</Trans>}
        </Menu.Item>
      </Menu.Dropdown>
    </Menu>
  )
}
