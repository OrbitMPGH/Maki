import { ActionIcon, Loader, Menu, Text, Tooltip } from '@mantine/core'
import { useState, type ReactElement } from 'react'
import {
    IconArchive,
    IconArchiveOff,
    IconBell,
    IconDeviceMobileDown,
    IconDeviceMobileOff,
    IconDotsVertical,
    IconEyeOff,
    IconFileText,
    IconFolderSymlink,
    IconEye,
    IconPencil,
    IconPhoto,
    IconRefresh,
    IconScan,
    IconSearch,
    IconSparkles,
    IconTrash,
    IconTrashX,
} from '@tabler/icons-react'
import { useIncognitoOptions } from '../ui/incognito'
import { useSeriesNotificationOptions } from '../ui/seriesNotifications'
import { msg } from '@lingui/core/macro'
import { Trans, useLingui } from '@lingui/react/macro'
import type { MessageDescriptor } from '@lingui/core'
import { useLabel } from '../../i18n-context'
import type { UpgradeProfileDto } from '../../api/upgrades'

/** Sentinel for "Instance default" in the profile RadioGroup, which only carries strings. */
const INSTANCE_DEFAULT = ''

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
                                      upgradeProfileId,
                                      upgradeProfiles,
                                      readFileCleanup,
                                      readFileCleanupActive,
                                      canEdit,
                                      canCleanUpReadFiles,
                                      canScanUpgrades,
                                      scanningUpgrades,
                                      searchingVolumes,
                                      busy,
                                      onRefreshChapters,
                                      onRefreshMetadata,
                                      onEditMetadata,
                                      onRescan,
                                      onMove,
                                      onRename,
                                      onSetMonitor,
                                      onSetIncognito,
                                      onSetNotify,
                                      onSetUpgradeProfile,
                                      onSetReadFileCleanup,
                                      onScanUpgrades,
                                      onSearchVolumes,
                                      canArchive,
                                      archived,
                                      sourceToolsDisabledReason = null,
                                      onArchive,
                                      onRestore,
                                      canRemove,
                                      onRemove,
                                      offlineSaved,
                                      onSaveUnreadOffline,
                                      onRemoveOffline,
                                  }: {
    monitorMode: string
    incognito: string
    notificationMode: string
    /** Null means "instance default", i.e. no profile pinned to this series. */
    upgradeProfileId: number | null
    upgradeProfiles: UpgradeProfileDto[]
    /** "Default" | "On" | "Off". */
    readFileCleanup: string
    /** The choice and the instance switch combined, for the value shown inline. */
    readFileCleanupActive: boolean
    /** EditMetadata: gates every item that changes the series itself. Notify is per user and stays. */
    canEdit: boolean
    /** DeleteSeries: "On" overrides the instance switch, and the cleanup deletes files for good. */
    canCleanUpReadFiles: boolean
    /** DownloadChapters: gates the "Scan for upgrades" item, same permission as "Upgrade now". */
    canScanUpgrades: boolean
    scanningUpgrades: boolean
    searchingVolumes: boolean
    busy: boolean
    onRefreshChapters: () => void
    onRefreshMetadata: () => void
    onEditMetadata: () => void
    onRescan: () => void
    onMove: () => void
    onRename: () => void
    onSetMonitor: (mode: string) => void
    onSetIncognito: (mode: string) => void
    onSetNotify: (mode: string) => void
    onSetUpgradeProfile: (upgradeProfileId: number | null) => void
    onSetReadFileCleanup: (mode: string) => void
    onScanUpgrades: () => void
    onSearchVolumes: () => void
    /** ArchiveSeries: gates both the Archive and the Restore to library entry. */
    canArchive: boolean
    archived: boolean
    /** Why the items that talk to a source are switched off (an archived series), or null while they work. */
    sourceToolsDisabledReason?: string | null
    onArchive: () => void
    onRestore: () => void
    canRemove: boolean
    onRemove: () => void
    /** Chapters of this series saved on this device; undefined where the browser cannot save any. */
    offlineSaved?: number
    onSaveUnreadOffline?: () => void
    onRemoveOffline?: () => void
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
    const instanceDefaultLabel = t`Instance default`
    const upgradeProfileValue = upgradeProfileId == null ? INSTANCE_DEFAULT : String(upgradeProfileId)
    const upgradeProfileLabel =
        upgradeProfiles.find((p) => p.id === upgradeProfileId)?.name ?? instanceDefaultLabel

    const toolsOff = sourceToolsDisabledReason !== null
    const offProps = {
        'data-disabled': toolsOff || undefined,
        'aria-disabled': toolsOff || undefined,
        closeMenuOnClick: !toolsOff,
    }
    const whenOn = (action: () => void) => () => {
        if (!toolsOff) action()
    }
    const withReason = (item: ReactElement) =>
        toolsOff ? (
            <Tooltip label={sourceToolsDisabledReason} position="left" withArrow>
                {item}
            </Tooltip>
        ) : (
            item
        )

    return (
        // Taller than a phone screen, so it opens toward the roomier side, stops at the screen edge
        // and scrolls inside. Scrolling the page instead would hide it once the button left view.
        <Menu
            position="bottom-end"
            width={264}
            withinPortal
            shadow="md"
            opened={opened}
            onChange={setOpened}
            middlewares={{ flip: { fallbackStrategy: 'bestFit' }, shift: true, size: { padding: 8 } }}
            classNames={{ dropdown: 'menu-dropdown-scroll' }}
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
                {canEdit && (
                    <>
                        <Menu.Label><Trans>Series</Trans></Menu.Label>
                        {withReason(
                            <Menu.Item leftSection={<IconRefresh size={16} />} onClick={whenOn(onRefreshChapters)} {...offProps}>
                                <Trans>Refresh chapters</Trans>
                            </Menu.Item>,
                        )}
                        <Menu.Item leftSection={<IconPhoto size={16} />} onClick={onRefreshMetadata}>
                            <Trans>Refresh metadata and poster</Trans>
                        </Menu.Item>
                        <Menu.Item leftSection={<IconPencil size={16} />} onClick={onEditMetadata}>
                            <Trans>Edit metadata</Trans>
                        </Menu.Item>

                        <Menu.Divider />
                    </>
                )}
                {(canEdit || canCleanUpReadFiles) && (
                    <>
                        <Menu.Label><Trans>Files</Trans></Menu.Label>
                        {canEdit && (
                            <>
                                <Menu.Item leftSection={<IconScan size={16} />} onClick={onRescan}>
                                    <Trans>Rescan files</Trans>
                                </Menu.Item>
                                <Menu.Item leftSection={<IconFolderSymlink size={16} />} onClick={onMove}>
                                    <Trans>Move to another root folder</Trans>
                                </Menu.Item>
                                <Menu.Item leftSection={<IconFileText size={16} />} onClick={onRename}>
                                    <Trans>Rename files</Trans>
                                </Menu.Item>
                            </>
                        )}
                        {canCleanUpReadFiles && (
                            <Menu.Sub floatingStrategy="fixed">
                                <Menu.Sub.Target>
                                    <Menu.Sub.Item
                                        leftSection={<IconTrashX size={16} />}
                                        rightSection={
                                            <Text size="xs" c="var(--ink-3)">
                                                {readFileCleanupActive ? t`On` : t`Off`}
                                            </Text>
                                        }
                                    >
                                        <Trans>Clean up read files</Trans>
                                    </Menu.Sub.Item>
                                </Menu.Sub.Target>
                                <Menu.Sub.Dropdown maw={264}>
                                    <Menu.Label>
                                        <Trans>Delete a chapter's file some days after everyone has read it</Trans>
                                    </Menu.Label>
                                    <Menu.RadioGroup value={readFileCleanup} onChange={onSetReadFileCleanup}>
                                        <Menu.RadioItem value="Default">
                                            <Trans>Follow the setting</Trans>
                                        </Menu.RadioItem>
                                        <Menu.RadioItem value="On">
                                            <Trans>On for this series</Trans>
                                        </Menu.RadioItem>
                                        <Menu.RadioItem value="Off">
                                            <Trans>Off for this series</Trans>
                                        </Menu.RadioItem>
                                    </Menu.RadioGroup>
                                    <Menu.Label><Trans>Read history stays, and the chapters aren't downloaded again.</Trans></Menu.Label>
                                </Menu.Sub.Dropdown>
                            </Menu.Sub>
                        )}

                        <Menu.Divider />
                    </>
                )}
                {offlineSaved !== undefined && (
                    <>
                        <Menu.Label><Trans>This device</Trans></Menu.Label>
                        <Menu.Item leftSection={<IconDeviceMobileDown size={16} />} onClick={onSaveUnreadOffline}>
                            <Trans>Save unread chapters for offline</Trans>
                        </Menu.Item>
                        <Menu.Item
                            leftSection={<IconDeviceMobileOff size={16} />}
                            disabled={offlineSaved === 0}
                            onClick={onRemoveOffline}
                        >
                            <Trans>Remove offline copies</Trans>
                        </Menu.Item>

                        <Menu.Divider />
                    </>
                )}
                <Menu.Label><Trans>Automation</Trans></Menu.Label>

                {canEdit && toolsOff && withReason(
                    <Menu.Item
                        leftSection={<IconEye size={16} />}
                        rightSection={
                            <Text size="xs" c="var(--ink-3)">
                                {renderLabel(label(MONITOR_OPTIONS, monitorMode))}
                            </Text>
                        }
                        {...offProps}
                    >
                        <Trans>Monitor</Trans>
                    </Menu.Item>,
                )}

                {canEdit && !toolsOff && (
                    <Menu.Sub floatingStrategy="fixed">
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
                )}

                {canEdit && (
                    <Menu.Sub floatingStrategy="fixed">
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
                )}

                <Menu.Sub floatingStrategy="fixed">
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

                {canEdit && (
                    <Menu.Sub floatingStrategy="fixed">
                        <Menu.Sub.Target>
                            <Menu.Sub.Item
                                leftSection={<IconSparkles size={16} />}
                                rightSection={
                                    <Text size="xs" c="var(--ink-3)">
                                        {upgradeProfileLabel}
                                    </Text>
                                }
                            >
                                <Trans>Quality profile</Trans>
                            </Menu.Sub.Item>
                        </Menu.Sub.Target>
                        <Menu.Sub.Dropdown maw={264}>
                            <Menu.RadioGroup
                                value={upgradeProfileValue}
                                onChange={(value) =>
                                    onSetUpgradeProfile(value === INSTANCE_DEFAULT ? null : Number(value))
                                }
                            >
                                <Menu.RadioItem value={INSTANCE_DEFAULT}>{instanceDefaultLabel}</Menu.RadioItem>
                                {upgradeProfiles.map((profile) => (
                                    <Menu.RadioItem key={profile.id} value={String(profile.id)}>
                                        {profile.name}
                                    </Menu.RadioItem>
                                ))}
                            </Menu.RadioGroup>
                        </Menu.Sub.Dropdown>
                    </Menu.Sub>
                )}

                {canScanUpgrades && (
                    withReason(
                        <Menu.Item
                            leftSection={scanningUpgrades ? <Loader size={14} /> : <IconRefresh size={16} />}
                            disabled={scanningUpgrades}
                            onClick={whenOn(onScanUpgrades)}
                            {...offProps}
                        >
                            <Trans>Scan for upgrades</Trans>
                        </Menu.Item>,
                    )
                )}

                {canScanUpgrades && (
                    withReason(
                        <Menu.Item
                            leftSection={searchingVolumes ? <Loader size={14} /> : <IconSearch size={16} />}
                            disabled={searchingVolumes}
                            onClick={whenOn(onSearchVolumes)}
                            {...offProps}
                        >
                            <Trans>Search volume releases</Trans>
                        </Menu.Item>,
                    )
                )}

                {(canArchive || canRemove) && <Menu.Divider />}

                {canArchive && (
                    <Menu.Item
                        leftSection={archived ? <IconArchiveOff size={16} /> : <IconArchive size={16} />}
                        closeMenuOnClick={false}
                        onClick={() => {
                            setOpened(false)
                            if (archived) onRestore()
                            else onArchive()
                        }}
                    >
                        {archived ? <Trans context="series">Restore to library</Trans> : <Trans context="series">Archive</Trans>}
                    </Menu.Item>
                )}

                {canRemove && (
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
                )}
            </Menu.Dropdown>
        </Menu>
    )
}
