import { ActionIcon, Menu, Text } from '@mantine/core'
import { useState } from 'react'
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
import { useIncognitoOptions } from '../ui/incognito'
import { useSeriesNotificationOptions } from '../ui/seriesNotifications'
import { msg } from '@lingui/core/macro'
import { Trans, useLingui } from '@lingui/react/macro'
import type { MessageDescriptor } from '@lingui/core'
import { useLabel } from '../../i18n-context'

/** Mirrors the labels the old monitor Select carried, so the toast after a change still matches. */
export const MONITOR_OPTIONS = [
    { value: 'All', label: msg`All chapters` },
    { value: 'Smart', label: msg`Smart` },
    { value: 'MainOnly', label: msg`Main only, no specials` },
    { value: 'None', label: msg`None` },
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
                                      canRemove,
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
    canRemove: boolean
    onRemove: () => void
}) {
    const [opened, setOpened] = useState(false)
    const { t } = useLingui()
    const renderLabel = useLabel()
    const incognitoOptions = useIncognitoOptions()
    const notificationOptions = useSeriesNotificationOptions()
    const label = (
        options: readonly { value: string; label: string | MessageDescriptor }[],
        value: string,
    ) => options.find((o) => o.value === value)?.label ?? value

    return (
        <Menu
            position="bottom-end"
            width={264}
            withinPortal
            shadow="md"
            opened={opened}
            onChange={setOpened}
        >
            <Menu.Target>
                <ActionIcon
                    variant="subtle"
                    size={42}
                    radius="md"
                    aria-label={t`More actions`}
                    disabled={busy}
                    style={{
                        color: 'var(--ink-3)',
                        background: 'color-mix(in srgb, var(--app-bg) 45%, transparent)',
                        border: '1px solid var(--border-strong)',
                    }}
                >
                    <IconDotsVertical size={19} />
                </ActionIcon>
            </Menu.Target>

            <Menu.Dropdown>
                <Menu.Label><Trans>Series</Trans></Menu.Label>
                <Menu.Item leftSection={<IconRefresh size={16} />} onClick={onRefreshChapters}>
                    <Trans>Refresh chapters</Trans>
                </Menu.Item>
                <Menu.Item leftSection={<IconPhoto size={16} />} onClick={onRefreshMetadata}>
                    <Trans>Refresh metadata and poster</Trans>
                </Menu.Item>

                <Menu.Divider />
                <Menu.Label><Trans>Files</Trans></Menu.Label>
                <Menu.Item leftSection={<IconScan size={16} />} onClick={onRescan}>
                    <Trans>Rescan files</Trans>
                </Menu.Item>
                <Menu.Item leftSection={<IconFolderSymlink size={16} />} onClick={onMove}>
                    <Trans>Move to another root folder</Trans>
                </Menu.Item>
                <Menu.Item leftSection={<IconFileText size={16} />} onClick={onRename}>
                    <Trans>Rename files</Trans>
                </Menu.Item>

                <Menu.Divider />
                <Menu.Label><Trans>Automation</Trans></Menu.Label>

                <Menu.Sub>
                    <Menu.Sub.Target>
                        <Menu.Sub.Item
                            leftSection={<IconEye size={16} />}
                            rightSection={
                                <Text size="xs" c="var(--ink-3)">
                                    {renderLabel(label(MONITOR_OPTIONS, monitorMode))}
                                </Text>
                            }
                        >
                            <Trans>Monitor</Trans>
                        </Menu.Sub.Item>
                    </Menu.Sub.Target>
                    <Menu.Sub.Dropdown maw={264}>
                        <Menu.Label><Trans>What happens to chapters released later</Trans></Menu.Label>
                        <Menu.RadioGroup value={monitorMode} onChange={onSetMonitor}>
                            {MONITOR_OPTIONS.map((o) => (
                                <Menu.RadioItem key={o.value} value={o.value}>
                                    {renderLabel(o.label)}
                                </Menu.RadioItem>
                            ))}
                        </Menu.RadioGroup>
                        <Menu.Label><Trans>Chapters already listed keep whatever you set on them.</Trans></Menu.Label>
                    </Menu.Sub.Dropdown>
                </Menu.Sub>

                <Menu.Sub>
                    <Menu.Sub.Target>
                        <Menu.Sub.Item
                            leftSection={<IconEyeOff size={16} />}
                            rightSection={
                                <Text size="xs" c="var(--ink-3)">
                                    {renderLabel(label(incognitoOptions, incognito))}
                                </Text>
                            }
                        >
                            <Trans>Incognito</Trans>
                        </Menu.Sub.Item>
                    </Menu.Sub.Target>
                    <Menu.Sub.Dropdown maw={264}>
                        <Menu.RadioGroup value={incognito} onChange={onSetIncognito}>
                            {incognitoOptions.map((o) => (
                                <Menu.RadioItem key={o.value} value={o.value}>
                                    {o.label}
                                </Menu.RadioItem>
                            ))}
                        </Menu.RadioGroup>
                        <Menu.Label>
                            <Trans>Scrobble only skips tracker pushes.</Trans>{' '}
                            <Trans>Full also excludes this series from Rewind stats and reading history.</Trans>
                        </Menu.Label>
                    </Menu.Sub.Dropdown>
                </Menu.Sub>

                <Menu.Sub>
                    <Menu.Sub.Target>
                        <Menu.Sub.Item
                            leftSection={<IconBell size={16} />}
                            rightSection={
                                <Text size="xs" c="var(--ink-3)">
                                    {renderLabel(label(notificationOptions, notificationMode))}
                                </Text>
                            }
                        >
                            <Trans>Notify</Trans>
                        </Menu.Sub.Item>
                    </Menu.Sub.Target>
                    <Menu.Sub.Dropdown maw={264}>
                        <Menu.RadioGroup value={notificationMode} onChange={onSetNotify}>
                            {notificationOptions.map((o) => (
                                <Menu.RadioItem key={o.value} value={o.value}>
                                    {o.label}
                                </Menu.RadioItem>
                            ))}
                        </Menu.RadioGroup>
                        <Menu.Label>
                            <Trans>While reading only tells you about new chapters while you are partway through.</Trans>{' '}
                            <Trans>Muted means nothing from this series at all.</Trans>
                        </Menu.Label>
                    </Menu.Sub.Dropdown>
                </Menu.Sub>

                {canRemove && (
                    <>
                        <Menu.Divider />
                        <Menu.Item
                            color="var(--danger)"
                            leftSection={<IconTrash size={16} />}
                            closeMenuOnClick={false}
                            onClick={() => {
                                setOpened(false)
                                onRemove()
                            }}
                        >
                            <Trans>Remove from library</Trans>
                        </Menu.Item>
                    </>
                )}
            </Menu.Dropdown>
        </Menu>
    )
}
