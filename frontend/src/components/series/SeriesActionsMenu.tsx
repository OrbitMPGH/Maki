import { ActionIcon, Group, Loader, Menu, Text, Tooltip } from '@mantine/core'
import { useEffect, useRef, useState, type ReactElement, type ReactNode } from 'react'
import {
    IconArchive,
    IconArchiveOff,
    IconArrowLeft,
    IconBell,
    IconChevronRight,
    IconDeviceMobile,
    IconDeviceMobileDown,
    IconDeviceMobileOff,
    IconDotsVertical,
    IconEyeOff,
    IconFileText,
    IconFolder,
    IconFolderSymlink,
    IconEye,
    IconPencil,
    IconPhoto,
    IconPhotoCog,
    IconRefresh,
    IconScan,
    IconSearch,
    IconSparkles,
    IconTrash,
    IconTrashX,
    IconArrowBackUp,
} from '@tabler/icons-react'
import { useIncognitoOptions } from '../ui/incognito'
import { useSeriesNotificationOptions } from '../ui/seriesNotifications'
import { msg } from '@lingui/core/macro'
import { Trans, useLingui } from '@lingui/react/macro'
import type { MessageDescriptor } from '@lingui/core'
import { useLabel } from '../../i18n-context'
import type { UpgradeProfileDto } from '../../api/upgrades'
import { PAGE_CONVERSION_STOPS, type PageConversionStop } from '../../api/pageConversion'
import { STOP_LABELS } from '../pageconversion/labels'
import { DrillPanes } from '../ui/DrillPanes'

/** Sentinel for "Instance default" in the profile RadioGroup, which only carries strings. */
const INSTANCE_DEFAULT = ''

/** The series endpoint names a fixed quality the way the enum does: "Balanced". */
const stopMode = (stop: PageConversionStop) => stop.charAt(0).toUpperCase() + stop.slice(1)

/** Mirrors the labels the old monitor Select carried, so the toast after a change still matches. */
export const MONITOR_OPTIONS = [
    { value: 'All', label: msg`All chapters` },
    { value: 'Smart', label: msg`Smart` },
    { value: 'MainOnly', label: msg`Main only, no specials` },
    { value: 'None', label: msg`None` },
] as const

type Page =
    | 'root'
    | 'files'
    | 'automation'
    | 'upgrades'
    | 'device'
    | 'monitor'
    | 'notify'
    | 'incognito'
    | 'profile'
    | 'cleanup'
    | 'conversion'

const PARENT: Record<Page, Page> = {
    root: 'root',
    files: 'root',
    automation: 'root',
    upgrades: 'root',
    device: 'root',
    monitor: 'automation',
    notify: 'automation',
    incognito: 'automation',
    profile: 'upgrades',
    cleanup: 'files',
    conversion: 'files',
}

/**
 * The series page's "More actions" menu: a short root list whose groups and settings open in place
 * behind a back row. The single long list with hover submenus it replaced outgrew the screen.
 * Picking a value returns to the group, so the new value shows next to the setting.
 */
export function SeriesActionsMenu({
                                      monitorMode,
                                      incognito,
                                      notificationMode,
                                      upgradeProfileId,
                                      upgradeProfiles,
                                      readFileCleanup,
                                      readFileCleanupActive,
                                      pageConversion,
                                      canSetPageConversion,
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
                                      onSetPageConversion,
                                      onUndoConversion,
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
    /** "Default" | "Never" | "Smallest" | "Compact" | "Balanced" | "High" | "Lossless". */
    pageConversion: string
    /** Admin: page conversion is a library-wide setting, so the per-series override is too. */
    canSetPageConversion: boolean
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
    onSetPageConversion: (mode: string) => void
    /** Set only when some of this series' converted files still have their original in the trash. */
    onUndoConversion?: () => void
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
    const [page, setPage] = useState<Page>('root')
    const dropdownRef = useRef<HTMLDivElement>(null)
    const cameFrom = useRef<Page | null>(null)
    const { t } = useLingui()
    const renderLabel = useLabel()
    const incognitoOptions = useIncognitoOptions()
    const notificationOptions = useSeriesNotificationOptions()
    const label = (
        options: readonly { value: string; label: string | MessageDescriptor }[],
        value: string,
    ) => options.find((o) => o.value === value)?.label ?? value
    const instanceDefaultLabel = t`Instance default`
    const pageConversionValue =
        pageConversion === 'Default'
            ? t`Rules`
            : pageConversion === 'Never'
              ? t`Never`
              : renderLabel(STOP_LABELS[pageConversion.toLowerCase() as PageConversionStop] ?? pageConversion)
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
            <Tooltip label={sourceToolsDisabledReason} position="left" withArrow events={{ hover: true, focus: true, touch: true }}>
                {item}
            </Tooltip>
        ) : (
            item
        )

    // A reader's Automation group would hold Notify alone, so for them it sits on the root list.
    const parentOf = (p: Page): Page => (p === 'notify' && !canEdit ? 'root' : PARENT[p])
    const depthOf = (p: Page): number => (p === 'root' ? 0 : 1 + depthOf(parentOf(p)))
    const go = (to: Page) => {
        cameFrom.current = page
        setPage(to)
    }

    // The clicked row unmounts with its page; without moving focus the arrow keys stop working. A
    // pointer user gets the dropdown itself, so no focus ring lands on a row they never keyed to.
    const viaKeyboard = useRef(false)
    useEffect(() => {
        const root = dropdownRef.current
        if (cameFrom.current === null || !root) return
        if (!viaKeyboard.current) return root.focus({ preventScroll: true })
        // The page sliding out is still in the dropdown, so look only in the one arriving.
        const pane = '[data-drill-pane]:not([inert])'
        const target =
            root.querySelector<HTMLElement>(`${pane} [data-page="${cameFrom.current}"]`) ??
            root.querySelector<HTMLElement>(`${pane} [data-menu-item]:not([data-disabled])`)
        target?.focus({ preventScroll: true })
    }, [page])

    const value = (text: ReactNode) => (
        <Text size="xs" c="var(--ink-3)" style={{ whiteSpace: 'nowrap' }}>
            {text}
        </Text>
    )
    const drill = (to: Page, icon: ReactNode, title: ReactNode, current?: ReactNode) => (
        <Menu.Item
            data-page={to}
            leftSection={icon}
            closeMenuOnClick={false}
            onClick={() => go(to)}
            rightSection={
                <Group gap={6} wrap="nowrap">
                    {current !== undefined && value(current)}
                    <IconChevronRight size={14} color="var(--ink-3)" />
                </Group>
            }
        >
            {title}
        </Menu.Item>
    )
    const back = (title: ReactNode) => (
        <Menu.Item
            className="series-actions-back"
            leftSection={
                <span className="series-actions-back-icon">
                    <IconArrowLeft size={16} />
                </span>
            }
            closeMenuOnClick={false}
            onClick={() => go(parentOf(page))}
            aria-label={t`Back`}
        >
            {title}
        </Menu.Item>
    )
    const choice = ({
                        title,
                        hint,
                        current,
                        options,
                        onPick,
                        note,
                    }: {
        title: ReactNode
        hint?: ReactNode
        current: string
        options: readonly { value: string; label: ReactNode }[]
        onPick: (value: string) => void
        note?: ReactNode
    }) => (
        <>
            {back(title)}
            {hint && <Menu.Label>{hint}</Menu.Label>}
            <Menu.RadioGroup
                value={current}
                onChange={(v) => {
                    onPick(v)
                    go(parentOf(page))
                }}
            >
                {options.map((o) => (
                    <Menu.RadioItem key={o.value} value={o.value} closeMenuOnClick={false}>
                        {o.label}
                    </Menu.RadioItem>
                ))}
            </Menu.RadioGroup>
            {note && <Menu.Label>{note}</Menu.Label>}
        </>
    )

    const hasFiles = canEdit || canCleanUpReadFiles || canSetPageConversion
    const hasUpgrades = canEdit || canScanUpgrades
    const monitorLabel = renderLabel(label(MONITOR_OPTIONS, monitorMode))
    const notifyRow = drill('notify', <IconBell size={16} />, <Trans>Notify</Trans>, renderLabel(label(notificationOptions, notificationMode)))

    const content = () => {
        switch (page) {
            case 'root':
                return (
                    <>
                        {canEdit && (
                            <>
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
                        {hasFiles && drill('files', <IconFolder size={16} />, <Trans>Files</Trans>)}
                        {canEdit ? drill('automation', <IconEye size={16} />, <Trans>Automation</Trans>) : notifyRow}
                        {hasUpgrades && drill('upgrades', <IconSparkles size={16} />, <Trans>Upgrades</Trans>)}
                        {offlineSaved !== undefined &&
                            drill('device', <IconDeviceMobile size={16} />, <Trans>This device</Trans>)}
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
                                className="series-actions-danger"
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
                    </>
                )
            case 'files':
                return (
                    <>
                        {back(<Trans>Files</Trans>)}
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
                        {canEdit && (canCleanUpReadFiles || canSetPageConversion) && <Menu.Divider />}
                        {canCleanUpReadFiles &&
                            drill('cleanup', <IconTrashX size={16} />, <Trans>Clean up read files</Trans>, readFileCleanupActive ? t`On` : t`Off`)}
                        {canSetPageConversion &&
                            drill('conversion', <IconPhotoCog size={16} />, <Trans>Page conversion</Trans>, pageConversionValue)}
                        {canSetPageConversion && onUndoConversion && (
                            <Menu.Item leftSection={<IconArrowBackUp size={16} />} onClick={onUndoConversion}>
                                <Trans>Undo conversion...</Trans>
                            </Menu.Item>
                        )}
                    </>
                )
            case 'automation':
                return (
                    <>
                        {back(<Trans>Automation</Trans>)}
                        {toolsOff
                            ? withReason(
                                <Menu.Item leftSection={<IconEye size={16} />} rightSection={value(monitorLabel)} {...offProps}>
                                    <Trans>Monitor</Trans>
                                </Menu.Item>,
                            )
                            : drill('monitor', <IconEye size={16} />, <Trans>Monitor</Trans>, monitorLabel)}
                        {notifyRow}
                        {drill('incognito', <IconEyeOff size={16} />, <Trans>Incognito</Trans>, renderLabel(label(incognitoOptions, incognito)))}
                    </>
                )
            case 'upgrades':
                return (
                    <>
                        {back(<Trans>Upgrades</Trans>)}
                        {canEdit && drill('profile', <IconSparkles size={16} />, <Trans>Quality profile</Trans>, upgradeProfileLabel)}
                        {canScanUpgrades &&
                            withReason(
                                <Menu.Item
                                    leftSection={scanningUpgrades ? <Loader size={14} /> : <IconRefresh size={16} />}
                                    disabled={scanningUpgrades}
                                    onClick={whenOn(onScanUpgrades)}
                                    {...offProps}
                                >
                                    <Trans>Scan for upgrades</Trans>
                                </Menu.Item>,
                            )}
                        {canScanUpgrades &&
                            withReason(
                                <Menu.Item
                                    leftSection={searchingVolumes ? <Loader size={14} /> : <IconSearch size={16} />}
                                    disabled={searchingVolumes}
                                    onClick={whenOn(onSearchVolumes)}
                                    {...offProps}
                                >
                                    <Trans>Search volume releases</Trans>
                                </Menu.Item>,
                            )}
                    </>
                )
            case 'device':
                return (
                    <>
                        {back(<Trans>This device</Trans>)}
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
                    </>
                )
            case 'monitor':
                return choice({
                    title: <Trans>Monitor</Trans>,
                    hint: <Trans>What happens to chapters released later</Trans>,
                    current: monitorMode,
                    options: MONITOR_OPTIONS.map((o) => ({ value: o.value, label: renderLabel(o.label) })),
                    onPick: onSetMonitor,
                })
            case 'notify':
                return choice({
                    title: <Trans>Notify</Trans>,
                    current: notificationMode,
                    options: notificationOptions,
                    onPick: onSetNotify,
                })
            case 'incognito':
                return choice({
                    title: <Trans>Incognito</Trans>,
                    current: incognito,
                    options: incognitoOptions,
                    onPick: onSetIncognito,
                    note: <Trans>Full also excludes this series from Rewind stats and reading history.</Trans>,
                })
            case 'profile':
                return choice({
                    title: <Trans>Quality profile</Trans>,
                    current: upgradeProfileValue,
                    options: [
                        { value: INSTANCE_DEFAULT, label: instanceDefaultLabel },
                        ...upgradeProfiles.map((p) => ({ value: String(p.id), label: p.name })),
                    ],
                    onPick: (v) => onSetUpgradeProfile(v === INSTANCE_DEFAULT ? null : Number(v)),
                })
            case 'cleanup':
                return choice({
                    title: <Trans>Clean up read files</Trans>,
                    hint: <Trans>Delete a chapter's file some days after everyone has read it</Trans>,
                    current: readFileCleanup,
                    options: [
                        { value: 'Default', label: <Trans>Follow the setting</Trans> },
                        { value: 'On', label: <Trans>On for this series</Trans> },
                        { value: 'Off', label: <Trans>Off for this series</Trans> },
                    ],
                    onPick: onSetReadFileCleanup,
                })
            case 'conversion':
                return choice({
                    title: <Trans>Page conversion</Trans>,
                    current: pageConversion,
                    options: [
                        { value: 'Default', label: <Trans>Follow the rules</Trans> },
                        { value: 'Never', label: <Trans>Never</Trans> },
                        ...PAGE_CONVERSION_STOPS.map((stop) => ({ value: stopMode(stop), label: renderLabel(STOP_LABELS[stop]) })),
                    ],
                    onPick: onSetPageConversion,
                    note: <Trans>Files already converted stay as they are.</Trans>,
                })
        }
    }

    return (
        // Capped at the screen edge with an inner scroll, for phones held sideways.
        <Menu
            position="bottom-end"
            width={288}
            withinPortal
            shadow="md"
            opened={opened}
            onChange={(next) => {
                if (next) {
                    cameFrom.current = null
                    setPage('root')
                }
                setOpened(next)
            }}
            middlewares={{ flip: { fallbackStrategy: 'bestFit' }, shift: true, size: { padding: 8 } }}
            classNames={{ dropdown: 'series-actions menu-dropdown-scroll' }}
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

            <Menu.Dropdown
                ref={dropdownRef}
                onPointerDown={() => {
                    viaKeyboard.current = false
                }}
                onKeyDown={(e) => {
                    viaKeyboard.current = true
                    if (e.key === 'ArrowLeft' && page !== 'root') {
                        e.preventDefault()
                        go(parentOf(page))
                    }
                }}
            >
                <DrillPanes page={page} depth={depthOf(page)} bleed={6}>
                    {content()}
                </DrillPanes>
            </Menu.Dropdown>
        </Menu>
    )
}
