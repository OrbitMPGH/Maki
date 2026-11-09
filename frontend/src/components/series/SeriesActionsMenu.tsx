import { ActionIcon, Group, Loader, Menu, Text, UnstyledButton, VisuallyHidden } from '@mantine/core'
import { useEffect, useId, useRef, useState, type KeyboardEvent, type ReactNode } from 'react'
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
  IconInfoCircle,
  IconEye,
  IconPencil,
  IconPhoto,
  IconPhotoCog,
  IconRefresh,
  IconScan,
  IconSearch,
  IconCircleArrowUp,
  IconTrash,
  IconTrashX,
  IconArrowBackUp,
  IconRanking,
} from '../../lib/icons'
import { useIncognitoOptions } from '../ui/incognito'
import { useSeriesNotificationHelp, type SeriesNotificationMode } from '../ui/seriesNotifications'
import { msg } from '@lingui/core/macro'
import { Trans, useLingui } from '@lingui/react/macro'
import type { MessageDescriptor } from '@lingui/core'
import { useLabel } from '../../i18n-context'
import type { UpgradeProfileDto } from '../../api/upgrades'
import { PAGE_CONVERSION_STOPS, type PageConversionStop } from '../../api/pageConversion'
import { STOP_HINTS, STOP_LABELS } from '../pageconversion/labels'
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

// Short names for the segmented controls, where the full labels don't fit four across.
export const MONITOR_SHORT: Record<string, MessageDescriptor> = {
    All: msg({ message: 'All', context: 'monitor mode' }),
    Smart: msg`Smart`,
    MainOnly: msg({ message: 'Main only', comment: 'Monitor mode that skips specials' }),
    None: msg`None`,
}

const MONITOR_HELP: Record<string, MessageDescriptor> = {
    All: msg`Every chapter released from now on is wanted.`,
    Smart: msg`Chapters released from now on are wanted and queued a batch at a time, ahead of where you're reading.`,
    MainOnly: msg`Chapters released from now on are wanted, except specials.`,
    None: msg`Chapters released from now on aren't wanted.`,
}

const NOTIFY_SHORT: Record<SeriesNotificationMode, MessageDescriptor> = {
    Default: msg`Default`,
    All: msg({ message: 'All', context: 'notify about every chapter' }),
    Reading: msg({ message: 'Reading', comment: 'Notify only while the user is partway through the series' }),
    Muted: msg`Muted`,
}

const INCOGNITO_HELP: Record<string, MessageDescriptor> = {
    Off: msg`Reading counts in your stats and goes to your trackers.`,
    ScrobbleOnly: msg`Reading counts in your stats but isn't sent to your trackers.`,
    Full: msg`Kept out of your trackers, Rewind, stats and reading history.`,
}

type Page =
    | 'root'
    | 'files'
    | 'automation'
    | 'upgrades'
    | 'device'
    | 'profile'
    | 'cleanup'
    | 'conversion'

const PARENT: Record<Page, Page> = {
    root: 'root',
    files: 'root',
    automation: 'root',
    upgrades: 'root',
    device: 'root',
    profile: 'upgrades',
    cleanup: 'files',
    conversion: 'files',
}

/**
 * The series page's "More actions" menu: a short root list whose groups and settings open in place
 * behind a back row. The single long list with hover submenus it replaced outgrew the screen.
 * Picking a value returns to the group, so the new value shows next to the setting. The
 * Automation settings are segmented controls on their group's page, one tap each.
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
                                      refreshingChapters = false,
                                      refreshingMetadata = false,
                                      rescanning = false,
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
    /** Each running action spins and waits on its own item; the rest of the menu stays usable. */
    refreshingChapters?: boolean
    refreshingMetadata?: boolean
    rescanning?: boolean
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
    const notifyHelp = useSeriesNotificationHelp(notificationMode as SeriesNotificationMode)
    const hintId = useId()
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
    // Said once, above the first item it switches off, rather than in a tooltip touch never shows.
    const reason = toolsOff && (
        <div className="series-actions-reason">
            <IconInfoCircle size={16} aria-hidden />
            <span>{sourceToolsDisabledReason}</span>
        </div>
    )

    const parentOf = (p: Page): Page => PARENT[p]
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
                    <IconChevronRight size={16} color="var(--ink-3)" />
                </Group>
            }
        >
            {title}
        </Menu.Item>
    )
    // Same header as the reader settings' pages. The hidden word keeps the page title in the name.
    const back = (title: ReactNode) => (
        <Menu.Item
            className="drill-back"
            leftSection={
                <span className="drill-back-icon" aria-hidden>
                    <IconArrowLeft size={16} />
                </span>
            }
            closeMenuOnClick={false}
            onClick={() => go(parentOf(page))}
        >
            <VisuallyHidden>{t`Back`}</VisuallyHidden>
            {title}
        </Menu.Item>
    )
    const choice = ({
                        title,
                        current,
                        options,
                        onPick,
                        note,
                    }: {
        title: ReactNode
        current: string
        options: readonly { value: string; label: ReactNode; hint?: ReactNode }[]
        onPick: (value: string) => void
        note?: ReactNode
    }) => (
        <>
            {back(title)}
            <Menu.RadioGroup
                value={current}
                onChange={(v) => {
                    onPick(v)
                    go(parentOf(page))
                }}
            >
                {options.map((o) => (
                    <Menu.RadioItem
                        key={o.value}
                        value={o.value}
                        closeMenuOnClick={false}
                        aria-describedby={o.hint ? `${hintId}-${o.value}` : undefined}
                    >
                        {o.label}
                        {/* Kept out of the name so an option is still called by its label, and read as its description. */}
                        {o.hint && (
                            <span id={`${hintId}-${o.value}`} className="series-actions-option-hint" aria-hidden>
                                {o.hint}
                            </span>
                        )}
                    </Menu.RadioItem>
                ))}
            </Menu.RadioGroup>
            {note && <Menu.Label>{note}</Menu.Label>}
        </>
    )
    const field = ({
                       icon,
                       title,
                       current,
                       options,
                       onPick,
                       help,
                       disabled = false,
                   }: {
        icon: ReactNode
        title: string
        current: string
        options: { value: string; label: string }[]
        onPick: (value: string) => void
        help?: ReactNode
        disabled?: boolean
    }) => (
        <div className="series-actions-field" data-disabled={disabled || undefined}>
            <div className="series-actions-field-title" aria-hidden>
                {icon}
                <span>{title}</span>
            </div>
            {/* Drawn as a segmented control but built from menu radio items: a menu may only hold
                items, groups and separators, so a real radio group in here would be invalid. */}
            <div role="group" aria-label={title} className="series-actions-segments">
                {options.map((o) => (
                    <UnstyledButton
                        key={o.value}
                        role="menuitemradio"
                        aria-checked={o.value === current}
                        data-checked={o.value === current || undefined}
                        disabled={disabled}
                        tabIndex={-1}
                        className="series-actions-segment"
                        onClick={() => o.value !== current && onPick(o.value)}
                    >
                        {o.label}
                    </UnstyledButton>
                ))}
            </div>
            {help && <div className="series-actions-field-help">{help}</div>}
        </div>
    )

    // Mantine's arrow keys only know its own items. Up and Down treat each segmented field as one
    // stop (its chosen segment) between the items; Left and Right move along a field's segments.
    const stepThroughFields = (e: KeyboardEvent) => {
        const target = e.target as HTMLElement
        if ((e.key === 'ArrowLeft' || e.key === 'ArrowRight') && target.matches('.series-actions-segment')) {
            const segments = Array.from(target.parentElement?.querySelectorAll<HTMLElement>('.series-actions-segment:not(:disabled)') ?? [])
            const at = segments.indexOf(target)
            e.preventDefault()
            e.stopPropagation()
            segments[(at + (e.key === 'ArrowRight' ? 1 : -1) + segments.length) % segments.length]?.focus()
            return
        }
        if (e.key !== 'ArrowDown' && e.key !== 'ArrowUp') return
        const pane = dropdownRef.current?.querySelector('[data-drill-pane]:not([inert])')
        if (!pane?.querySelector('.series-actions-field')) return
        const stops = Array.from(
            pane.querySelectorAll<HTMLElement>('[data-menu-item]:not([data-disabled]), .series-actions-field:not([data-disabled])'),
        )
            .map((el) =>
                el.matches('.series-actions-field')
                    ? (el.querySelector<HTMLElement>('.series-actions-segment[data-checked]') ??
                      el.querySelector<HTMLElement>('.series-actions-segment'))
                    : el,
            )
            .filter((el): el is HTMLElement => el !== null)
        if (stops.length === 0) return
        e.preventDefault()
        e.stopPropagation()
        const field = target.closest('.series-actions-field')
        const at = stops.findIndex((el) => el === target || (field !== null && field.contains(el)))
        const step = e.key === 'ArrowDown' ? 1 : -1
        const next = at === -1 ? (step === 1 ? 0 : stops.length - 1) : (at + step + stops.length) % stops.length
        stops[next].focus()
    }

    const hasFiles = canEdit || canCleanUpReadFiles || canSetPageConversion
    const hasUpgrades = canEdit || canScanUpgrades
    const notifyField = field({
        icon: <IconBell size={16} />,
        title: t`Notify`,
        current: notificationMode,
        options: Object.entries(NOTIFY_SHORT).map(([mode, short]) => ({ value: mode, label: renderLabel(short) })),
        onPick: onSetNotify,
        help: notifyHelp,
    })

    const content = () => {
        switch (page) {
            case 'root':
                return (
                    <>
                        {canEdit && (
                            <>
                                {reason}
                                <Menu.Item
                                    leftSection={refreshingChapters ? <Loader size={16} /> : <IconRefresh size={16} />}
                                    disabled={refreshingChapters}
                                    onClick={whenOn(onRefreshChapters)}
                                    {...offProps}
                                >
                                    <Trans>Refresh chapters</Trans>
                                </Menu.Item>
                                <Menu.Item
                                    leftSection={refreshingMetadata ? <Loader size={16} /> : <IconPhoto size={16} />}
                                    disabled={refreshingMetadata}
                                    onClick={onRefreshMetadata}
                                >
                                    <Trans>Refresh metadata and poster</Trans>
                                </Menu.Item>
                                <Menu.Item leftSection={<IconPencil size={16} />} onClick={onEditMetadata}>
                                    <Trans>Edit metadata</Trans>
                                </Menu.Item>
                                <Menu.Divider />
                            </>
                        )}
                        {hasFiles && drill('files', <IconFolder size={16} />, <Trans>Files</Trans>)}
                        {/* A reader's Automation page would hold Notify alone, so for them it sits here. */}
                        {canEdit ? drill('automation', <IconEye size={16} />, <Trans>Automation</Trans>) : notifyField}
                        {hasUpgrades && drill('upgrades', <IconCircleArrowUp size={16} />, <Trans>Upgrades</Trans>)}
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
                                <Menu.Item
                                    leftSection={rescanning ? <Loader size={16} /> : <IconScan size={16} />}
                                    disabled={rescanning}
                                    onClick={onRescan}
                                >
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
                        {reason}
                        {field({
                            icon: <IconEye size={16} />,
                            title: t`Monitor`,
                            current: monitorMode,
                            options: MONITOR_OPTIONS.map((o) => ({ value: o.value, label: renderLabel(MONITOR_SHORT[o.value]) })),
                            onPick: onSetMonitor,
                            help: MONITOR_HELP[monitorMode] && renderLabel(MONITOR_HELP[monitorMode]),
                            disabled: toolsOff,
                        })}
                        {notifyField}
                        {field({
                            icon: <IconEyeOff size={16} />,
                            title: t`Incognito`,
                            current: incognito,
                            options: incognitoOptions,
                            onPick: onSetIncognito,
                            help: INCOGNITO_HELP[incognito] && renderLabel(INCOGNITO_HELP[incognito]),
                        })}
                    </>
                )
            case 'upgrades':
                return (
                    <>
                        {back(<Trans>Upgrades</Trans>)}
                        {canScanUpgrades && reason}
                        {canEdit && drill('profile', <IconRanking size={16} />, <Trans>Quality profile</Trans>, upgradeProfileLabel)}
                        {canScanUpgrades && (
                            <Menu.Item
                                leftSection={scanningUpgrades ? <Loader size={16} /> : <IconRefresh size={16} />}
                                disabled={scanningUpgrades}
                                onClick={whenOn(onScanUpgrades)}
                                {...offProps}
                            >
                                <Trans>Scan for upgrades</Trans>
                            </Menu.Item>
                        )}
                        {canScanUpgrades && (
                            <Menu.Item
                                leftSection={searchingVolumes ? <Loader size={16} /> : <IconSearch size={16} />}
                                disabled={searchingVolumes}
                                onClick={whenOn(onSearchVolumes)}
                                {...offProps}
                            >
                                <Trans>Search volume releases</Trans>
                            </Menu.Item>
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
                    current: readFileCleanup,
                    options: [
                        {
                            value: 'Default',
                            label: <Trans>Use the Downloads setting</Trans>,
                            // Only while it is the choice does the combined value say what the setting is.
                            hint:
                                readFileCleanup === 'Default' &&
                                (readFileCleanupActive ? <Trans>It's on there now.</Trans> : <Trans>It's off there now.</Trans>),
                        },
                        {
                            value: 'On',
                            label: <Trans>On for this series</Trans>,
                            hint: <Trans>Each chapter's file is deleted for good some days after everyone has read it.</Trans>,
                        },
                        { value: 'Off', label: <Trans>Off for this series</Trans>, hint: <Trans>Read files stay on disk.</Trans> },
                    ],
                    onPick: onSetReadFileCleanup,
                })
            case 'conversion':
                return choice({
                    title: <Trans>Page conversion</Trans>,
                    current: pageConversion,
                    options: [
                        {
                            value: 'Default',
                            label: <Trans>Follow the rules</Trans>,
                            hint: <Trans>The page conversion rules in Settings decide.</Trans>,
                        },
                        { value: 'Never', label: <Trans>Never</Trans>, hint: <Trans>Pages stay as they were downloaded.</Trans> },
                        ...PAGE_CONVERSION_STOPS.map((stop) => ({
                            value: stopMode(stop),
                            label: renderLabel(STOP_LABELS[stop]),
                            hint: renderLabel(STOP_HINTS[stop]),
                        })),
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
            width={304}
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
                <div onKeyDownCapture={stepThroughFields}>
                    <DrillPanes page={page} depth={depthOf(page)} bleed={6}>
                        {content()}
                    </DrillPanes>
                </div>
            </Menu.Dropdown>
        </Menu>
    )
}
