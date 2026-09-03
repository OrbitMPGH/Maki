import { ActionIcon, Menu, Text } from '@mantine/core'
import {
  IconBell,
  IconDotsVertical,
  IconEyeOff,
  IconFileText,
  IconFolderSymlink,
  IconEye,
  IconPhoto,
  IconRefresh,
  IconScan,
  IconTrash,
} from '@tabler/icons-react'
import { INCOGNITO_OPTIONS } from '../ui/incognito'
import { SERIES_NOTIFICATION_OPTIONS } from '../ui/seriesNotifications'

/** Mirrors the labels the old monitor Select carried, so the toast after a change still matches. */
export const MONITOR_OPTIONS = [
  { value: 'All', label: 'All chapters' },
  { value: 'Smart', label: 'Smart' },
  { value: 'MainOnly', label: 'Main only, no specials' },
  { value: 'None', label: 'None' },
] as const

/**
 * Everything that used to be an eleven-button toolbar under the hero.
 *
 * Grouped by what it touches rather than by how often it is used, because the grouping is the only
 * thing standing in for the labels the buttons used to carry. The three settings that were
 * `Select`s are submenus with their current value shown inline, and each keeps the explanatory
 * line the old tooltip carried: "what happens to chapters released later" is not obvious from the
 * word Monitor, and losing it to a redesign would be a real regression.
 */
export function SeriesActionsMenu({
  monitorMode,
  incognito,
  notificationMode,
  busy,
  onRefreshChapters,
  onRefreshMetadata,
  onRescan,
  onMove,
  onRename,
  onSetMonitor,
  onSetIncognito,
  onSetNotify,
  onRemove,
}: {
  monitorMode: string
  incognito: string
  notificationMode: string
  busy: boolean
  onRefreshChapters: () => void
  onRefreshMetadata: () => void
  onRescan: () => void
  onMove: () => void
  onRename: () => void
  onSetMonitor: (mode: string) => void
  onSetIncognito: (mode: string) => void
  onSetNotify: (mode: string) => void
  onRemove: () => void
}) {
  const label = (options: readonly { value: string; label: string }[], value: string) =>
    options.find((o) => o.value === value)?.label ?? value

  return (
    <Menu position="bottom-end" width={264} withinPortal shadow="md">
      <Menu.Target>
        <ActionIcon
          variant="default"
          size={42}
          radius="md"
          aria-label="More actions"
          disabled={busy}
        >
          <IconDotsVertical size={19} />
        </ActionIcon>
      </Menu.Target>

      <Menu.Dropdown>
        <Menu.Label>Series</Menu.Label>
        <Menu.Item leftSection={<IconRefresh size={16} />} onClick={onRefreshChapters}>
          Refresh chapters
        </Menu.Item>
        <Menu.Item leftSection={<IconPhoto size={16} />} onClick={onRefreshMetadata}>
          Refresh metadata and poster
        </Menu.Item>

        <Menu.Divider />
        <Menu.Label>Files</Menu.Label>
        <Menu.Item leftSection={<IconScan size={16} />} onClick={onRescan}>
          Rescan files
        </Menu.Item>
        <Menu.Item leftSection={<IconFolderSymlink size={16} />} onClick={onMove}>
          Move to another root folder
        </Menu.Item>
        <Menu.Item leftSection={<IconFileText size={16} />} onClick={onRename}>
          Rename files
        </Menu.Item>

        <Menu.Divider />
        <Menu.Label>Automation</Menu.Label>

        <Menu.Sub>
          <Menu.Sub.Target>
            <Menu.Sub.Item
              leftSection={<IconEye size={16} />}
              rightSection={
                <Text size="xs" c="dimmed">
                  {label(MONITOR_OPTIONS, monitorMode)}
                </Text>
              }
            >
              Monitor
            </Menu.Sub.Item>
          </Menu.Sub.Target>
          <Menu.Sub.Dropdown>
            <Menu.Label>What happens to chapters released later</Menu.Label>
            <Menu.RadioGroup value={monitorMode} onChange={onSetMonitor}>
              {MONITOR_OPTIONS.map((o) => (
                <Menu.RadioItem key={o.value} value={o.value}>
                  {o.label}
                </Menu.RadioItem>
              ))}
            </Menu.RadioGroup>
            <Menu.Label>Chapters already listed keep whatever you set on them.</Menu.Label>
          </Menu.Sub.Dropdown>
        </Menu.Sub>

        <Menu.Sub>
          <Menu.Sub.Target>
            <Menu.Sub.Item
              leftSection={<IconEyeOff size={16} />}
              rightSection={
                <Text size="xs" c="dimmed">
                  {label(INCOGNITO_OPTIONS, incognito)}
                </Text>
              }
            >
              Incognito
            </Menu.Sub.Item>
          </Menu.Sub.Target>
          <Menu.Sub.Dropdown>
            <Menu.RadioGroup value={incognito} onChange={onSetIncognito}>
              {INCOGNITO_OPTIONS.map((o) => (
                <Menu.RadioItem key={o.value} value={o.value}>
                  {o.label}
                </Menu.RadioItem>
              ))}
            </Menu.RadioGroup>
            <Menu.Label>
              Scrobble only skips tracker pushes. Full also excludes this series from Rewind stats
              and reading history.
            </Menu.Label>
          </Menu.Sub.Dropdown>
        </Menu.Sub>

        <Menu.Sub>
          <Menu.Sub.Target>
            <Menu.Sub.Item
              leftSection={<IconBell size={16} />}
              rightSection={
                <Text size="xs" c="dimmed">
                  {label(SERIES_NOTIFICATION_OPTIONS, notificationMode)}
                </Text>
              }
            >
              Notify
            </Menu.Sub.Item>
          </Menu.Sub.Target>
          <Menu.Sub.Dropdown>
            <Menu.RadioGroup value={notificationMode} onChange={onSetNotify}>
              {SERIES_NOTIFICATION_OPTIONS.map((o) => (
                <Menu.RadioItem key={o.value} value={o.value}>
                  {o.label}
                </Menu.RadioItem>
              ))}
            </Menu.RadioGroup>
            <Menu.Label>
              While reading only tells you about new chapters while you are partway through. Muted
              means nothing from this series at all.
            </Menu.Label>
          </Menu.Sub.Dropdown>
        </Menu.Sub>

        <Menu.Divider />
        <Menu.Item color="red" leftSection={<IconTrash size={16} />} onClick={onRemove}>
          Remove from library
        </Menu.Item>
      </Menu.Dropdown>
    </Menu>
  )
}
