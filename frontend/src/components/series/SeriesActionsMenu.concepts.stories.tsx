import { useState, type ReactNode } from 'react'
import { ActionIcon, Group, Menu, Popover, SegmentedControl, Select, SimpleGrid, Stack, Text } from '@mantine/core'
import {
  IconAdjustmentsHorizontal,
  IconArchive,
  IconArrowBackUp,
  IconBell,
  IconChevronLeft,
  IconChevronRight,
  IconDeviceMobile,
  IconDeviceMobileDown,
  IconDeviceMobileOff,
  IconDotsVertical,
  IconEye,
  IconEyeOff,
  IconFileText,
  IconFolder,
  IconFolderSymlink,
  IconPencil,
  IconPhoto,
  IconPhotoCog,
  IconRefresh,
  IconScan,
  IconSearch,
  IconSparkles,
  IconTrash,
  IconTrashX,
} from '@tabler/icons-react'
import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { upgradeProfiles } from '../../../.storybook/mocks/series'
import { SeriesActionsMenu } from './SeriesActionsMenu'

// Design concepts for the series page's "More actions" menu, which is taller than most screens.
// Self-contained mockups: nothing here is wired into the app.

type Option = { value: string; label: string; short?: string; help?: string }
type SettingKey = 'monitor' | 'notify' | 'incognito' | 'profile' | 'cleanup' | 'conversion'
type Settings = Record<SettingKey, string>
type SettingDef = { key: SettingKey; label: string; icon: ReactNode; hint: string; foot?: string; options: Option[] }

const SETTINGS: Record<SettingKey, SettingDef> = {
  monitor: {
    key: 'monitor',
    label: 'Monitor',
    icon: <IconEye size={16} />,
    hint: 'What happens to chapters released later',
    foot: 'Chapters already listed keep whatever you set on them.',
    options: [
      { value: 'All', label: 'All chapters' },
      { value: 'Smart', label: 'Smart' },
      { value: 'MainOnly', label: 'Main only, no specials', short: 'Main only' },
      { value: 'None', label: 'None' },
    ],
  },
  notify: {
    key: 'notify',
    label: 'Notify',
    icon: <IconBell size={16} />,
    hint: 'New chapter notifications for you',
    foot: 'While reading only tells you about new chapters while you are partway through. Muted means nothing from this series at all.',
    options: [
      { value: 'Default', label: 'Default', help: 'Follows the default on your notification settings.' },
      { value: 'All', label: 'Every chapter', help: 'Tells you about every new chapter, whatever your default is.' },
      { value: 'Reading', label: 'While reading', help: "Only while you're partway through the series and haven't marked it finished." },
      { value: 'Muted', label: 'Muted', help: 'Nothing from this series. Admins still get download failures.' },
    ],
  },
  incognito: {
    key: 'incognito',
    label: 'Incognito',
    icon: <IconEyeOff size={16} />,
    hint: 'What your reading of this series leaves behind',
    foot: 'Scrobble only skips tracker pushes. Full also excludes this series from Rewind stats and reading history.',
    options: [
      { value: 'Off', label: 'Off', help: 'Scrobbles, stats and history as usual.' },
      { value: 'ScrobbleOnly', label: 'No scrobble', help: 'Skips tracker pushes.' },
      { value: 'Full', label: 'Full', help: 'Also left out of Rewind stats and reading history.' },
    ],
  },
  profile: {
    key: 'profile',
    label: 'Quality profile',
    icon: <IconSparkles size={16} />,
    hint: 'Which releases count as an upgrade',
    options: [
      { value: '', label: 'Instance default', short: 'Default', help: 'Uses the profile set in Settings.' },
      ...upgradeProfiles.map((p) => ({ value: String(p.id), label: p.name, help: p.description ?? undefined })),
    ],
  },
  cleanup: {
    key: 'cleanup',
    label: 'Clean up read files',
    icon: <IconTrashX size={16} />,
    hint: "Delete a chapter's file some days after everyone has read it",
    foot: "Read history stays, and the chapters aren't downloaded again.",
    options: [
      { value: 'Default', label: 'Follow the setting', short: 'Setting' },
      { value: 'On', label: 'On for this series', short: 'On' },
      { value: 'Off', label: 'Off for this series', short: 'Off' },
    ],
  },
  conversion: {
    key: 'conversion',
    label: 'Page conversion',
    icon: <IconPhotoCog size={16} />,
    hint: "How this series' pages are stored",
    foot: 'Wins over the rules. Files already converted stay as they are.',
    options: [
      { value: 'Default', label: 'Follow the rules', short: 'Rules' },
      { value: 'Never', label: 'Never' },
      ...['Smallest', 'Compact', 'Balanced', 'High', 'Lossless'].map((v) => ({ value: v, label: v })),
    ],
  },
}

const INITIAL: Settings = { monitor: 'All', notify: 'Default', incognito: 'Off', profile: '', cleanup: 'Default', conversion: 'Default' }

const optionOf = (key: SettingKey, value: string) => SETTINGS[key].options.find((o) => o.value === value)
const shortLabel = (key: SettingKey, value: string) => {
  const o = optionOf(key, value)
  return o?.short ?? o?.label ?? value
}

type ConceptProps = {
  onAction: (action: string) => void
  onSetting: (key: SettingKey, value: string) => void
  reader?: boolean
}

function useConceptSettings(onSetting: ConceptProps['onSetting']) {
  const [settings, setSettings] = useState(INITIAL)
  const set = (key: SettingKey, value: string) => {
    onSetting(key, value)
    setSettings((s) => ({ ...s, [key]: value }))
  }
  return [settings, set] as const
}

const triggerStyle = {
  color: 'var(--ink-3)',
  background: 'color-mix(in srgb, var(--app-bg) 45%, transparent)',
  border: '1px solid var(--border-strong)',
}

const Value = ({ children }: { children: ReactNode }) => (
  <Text size="xs" c="var(--ink-3)">
    {children}
  </Text>
)

/* ---------------------------------------------------------------------------------------------- */
/* A. Split: one-shot actions stay in the menu, the six settings move to their own popover.        */
/* ---------------------------------------------------------------------------------------------- */

function SettingRow({ def, value, onChange }: { def: SettingDef; value: string; onChange: (v: string) => void }) {
  const help = optionOf(def.key, value)?.help ?? def.hint
  const segmented = def.key === 'incognito' || def.key === 'cleanup'
  return (
    <Stack gap={6}>
      <Group justify="space-between" wrap="nowrap" gap="sm">
        <Group gap={8} wrap="nowrap" c="var(--ink-3)">
          {def.icon}
          <Text size="sm" fw={500} c="var(--mantine-color-text)" style={{ whiteSpace: 'nowrap' }}>
            {def.label}
          </Text>
        </Group>
        {!segmented && (
          <Select
            size="xs"
            w={156}
            aria-label={def.label}
            allowDeselect={false}
            data={def.options.map((o) => ({ value: o.value, label: o.label }))}
            value={value}
            onChange={(v) => v !== null && onChange(v)}
            comboboxProps={{ withinPortal: false }}
          />
        )}
      </Group>
      {segmented && (
        <SegmentedControl
          size="xs"
          fullWidth
          aria-label={def.label}
          data={def.options.map((o) => ({ value: o.value, label: o.short ?? o.label }))}
          value={value}
          onChange={onChange}
        />
      )}
      <Text size="xs" c="var(--ink-3)" lh={1.35}>
        {help}
      </Text>
    </Stack>
  )
}

function SettingsGroup({ title, children }: { title: string; children: ReactNode }) {
  return (
    <Stack gap="sm">
      <Text size="xs" fw={600} tt="uppercase" c="var(--ink-3)" style={{ letterSpacing: '0.04em' }}>
        {title}
      </Text>
      {children}
    </Stack>
  )
}

function SplitConcept({ onAction, onSetting, reader }: ConceptProps) {
  const [s, set] = useConceptSettings(onSetting)
  const row = (key: SettingKey) => <SettingRow key={key} def={SETTINGS[key]} value={s[key]} onChange={(v) => set(key, v)} />
  const item = (action: string, icon: ReactNode, color?: string) => (
    <Menu.Item leftSection={icon} color={color} onClick={() => onAction(action)}>
      {action}
    </Menu.Item>
  )
  return (
    <Group gap="xs">
      <Popover position="bottom-end" width={360} shadow="md" withinPortal>
        <Popover.Target>
          <ActionIcon variant="subtle" size={42} radius="md" aria-label="Series settings" style={triggerStyle}>
            <IconAdjustmentsHorizontal size={19} />
          </ActionIcon>
        </Popover.Target>
        <Popover.Dropdown p="md">
          <Stack gap="lg">
            {!reader && (
              <SettingsGroup title="For everyone">
                {row('monitor')}
                {row('profile')}
                {row('conversion')}
                {row('cleanup')}
              </SettingsGroup>
            )}
            <SettingsGroup title="Just for you">
              {row('notify')}
              {row('incognito')}
            </SettingsGroup>
          </Stack>
        </Popover.Dropdown>
      </Popover>

      <Menu position="bottom-end" width={272} withinPortal shadow="md">
        <Menu.Target>
          <ActionIcon variant="subtle" size={42} radius="md" aria-label="More actions" style={triggerStyle}>
            <IconDotsVertical size={19} />
          </ActionIcon>
        </Menu.Target>
        <Menu.Dropdown>
          {!reader && (
            <>
              {item('Refresh chapters', <IconRefresh size={16} />)}
              {item('Refresh metadata and poster', <IconPhoto size={16} />)}
              {item('Edit metadata', <IconPencil size={16} />)}
              <Menu.Divider />
              {item('Rescan files', <IconScan size={16} />)}
              {item('Move to another root folder', <IconFolderSymlink size={16} />)}
              {item('Rename files', <IconFileText size={16} />)}
              {item('Undo conversion...', <IconArrowBackUp size={16} />)}
              <Menu.Divider />
              {item('Scan for upgrades', <IconRefresh size={16} />)}
              {item('Search volume releases', <IconSearch size={16} />)}
              <Menu.Divider />
            </>
          )}
          {item('Save unread chapters for offline', <IconDeviceMobileDown size={16} />)}
          {item('Remove offline copies', <IconDeviceMobileOff size={16} />)}
          {!reader && (
            <>
              <Menu.Divider />
              {item('Archive', <IconArchive size={16} />)}
              {item('Remove from library', <IconTrash size={16} />, 'var(--danger)')}
            </>
          )}
        </Menu.Dropdown>
      </Menu>
    </Group>
  )
}

/* ---------------------------------------------------------------------------------------------- */
/* B. Drill-down: a short root list, groups open in place with a back row. No hover submenus.      */
/* ---------------------------------------------------------------------------------------------- */

type Page = 'root' | 'files' | 'automation' | 'upgrades' | 'device' | SettingKey

const PARENT: Record<SettingKey, Page> = {
  monitor: 'automation',
  notify: 'automation',
  incognito: 'automation',
  profile: 'upgrades',
  cleanup: 'files',
  conversion: 'files',
}

function DrillDownConcept({ onAction, onSetting }: ConceptProps) {
  const [s, set] = useConceptSettings(onSetting)
  const [opened, setOpened] = useState(false)
  const [page, setPage] = useState<Page>('root')

  const item = (action: string, icon: ReactNode, color?: string) => (
    <Menu.Item leftSection={icon} color={color} onClick={() => onAction(action)}>
      {action}
    </Menu.Item>
  )
  const drill = (to: Page, label: string, icon: ReactNode, value?: string) => (
    <Menu.Item
      leftSection={icon}
      closeMenuOnClick={false}
      onClick={() => setPage(to)}
      rightSection={
        <Group gap={4} wrap="nowrap" c="var(--ink-3)">
          {value && <Value>{value}</Value>}
          <IconChevronRight size={14} />
        </Group>
      }
    >
      {label}
    </Menu.Item>
  )
  const setting = (key: SettingKey) => drill(key, SETTINGS[key].label, SETTINGS[key].icon, shortLabel(key, s[key]))
  const back = (to: Page, title: string) => (
    <>
      <Menu.Item leftSection={<IconChevronLeft size={16} />} closeMenuOnClick={false} onClick={() => setPage(to)} fw={600}>
        {title}
      </Menu.Item>
      <Menu.Divider />
    </>
  )

  const content = () => {
    switch (page) {
      case 'root':
        return (
          <>
            {item('Refresh chapters', <IconRefresh size={16} />)}
            {item('Refresh metadata and poster', <IconPhoto size={16} />)}
            {item('Edit metadata', <IconPencil size={16} />)}
            <Menu.Divider />
            {drill('files', 'Files', <IconFolder size={16} />)}
            {drill('automation', 'Monitoring and alerts', <IconEye size={16} />)}
            {drill('upgrades', 'Upgrades', <IconSparkles size={16} />)}
            {drill('device', 'This device', <IconDeviceMobile size={16} />)}
            <Menu.Divider />
            {item('Archive', <IconArchive size={16} />)}
            {item('Remove from library', <IconTrash size={16} />, 'var(--danger)')}
          </>
        )
      case 'files':
        return (
          <>
            {back('root', 'Files')}
            {item('Rescan files', <IconScan size={16} />)}
            {item('Move to another root folder', <IconFolderSymlink size={16} />)}
            {item('Rename files', <IconFileText size={16} />)}
            <Menu.Divider />
            {setting('cleanup')}
            {setting('conversion')}
            {item('Undo conversion...', <IconArrowBackUp size={16} />)}
          </>
        )
      case 'automation':
        return (
          <>
            {back('root', 'Monitoring and alerts')}
            {setting('monitor')}
            {setting('notify')}
            {setting('incognito')}
          </>
        )
      case 'upgrades':
        return (
          <>
            {back('root', 'Upgrades')}
            {setting('profile')}
            {item('Scan for upgrades', <IconRefresh size={16} />)}
            {item('Search volume releases', <IconSearch size={16} />)}
          </>
        )
      case 'device':
        return (
          <>
            {back('root', 'This device')}
            {item('Save unread chapters for offline', <IconDeviceMobileDown size={16} />)}
            {item('Remove offline copies', <IconDeviceMobileOff size={16} />)}
          </>
        )
      default: {
        const def = SETTINGS[page]
        return (
          <>
            {back(PARENT[page], def.label)}
            <Menu.Label>{def.hint}</Menu.Label>
            <Menu.RadioGroup
              value={s[page]}
              onChange={(v) => {
                set(page, v)
                setPage(PARENT[page])
              }}
            >
              {def.options.map((o) => (
                <Menu.RadioItem key={o.value} value={o.value} closeMenuOnClick={false}>
                  {o.label}
                </Menu.RadioItem>
              ))}
            </Menu.RadioGroup>
            {def.foot && <Menu.Label>{def.foot}</Menu.Label>}
          </>
        )
      }
    }
  }

  return (
    <Menu
      position="bottom-end"
      width={272}
      withinPortal
      shadow="md"
      opened={opened}
      onChange={(o) => {
        setOpened(o)
        if (!o) setPage('root')
      }}
    >
      <Menu.Target>
        <ActionIcon variant="subtle" size={42} radius="md" aria-label="More actions" style={triggerStyle}>
          <IconDotsVertical size={19} />
        </ActionIcon>
      </Menu.Target>
      <Menu.Dropdown>{content()}</Menu.Dropdown>
    </Menu>
  )
}

/* ---------------------------------------------------------------------------------------------- */
/* C. Wide panel: same items and submenus as today, laid out in two columns with a footer row.     */
/* ---------------------------------------------------------------------------------------------- */

function WidePanelConcept({ onAction, onSetting }: ConceptProps) {
  const [s, set] = useConceptSettings(onSetting)
  const item = (action: string, icon: ReactNode, color?: string) => (
    <Menu.Item leftSection={icon} color={color} onClick={() => onAction(action)}>
      {action}
    </Menu.Item>
  )
  const sub = (key: SettingKey) => {
    const def = SETTINGS[key]
    return (
      <Menu.Sub floatingStrategy="fixed">
        <Menu.Sub.Target>
          <Menu.Sub.Item leftSection={def.icon} rightSection={<Value>{shortLabel(key, s[key])}</Value>}>
            {def.label}
          </Menu.Sub.Item>
        </Menu.Sub.Target>
        <Menu.Sub.Dropdown maw={264}>
          <Menu.Label>{def.hint}</Menu.Label>
          <Menu.RadioGroup value={s[key]} onChange={(v) => set(key, v)}>
            {def.options.map((o) => (
              <Menu.RadioItem key={o.value} value={o.value}>
                {o.label}
              </Menu.RadioItem>
            ))}
          </Menu.RadioGroup>
          {def.foot && <Menu.Label>{def.foot}</Menu.Label>}
        </Menu.Sub.Dropdown>
      </Menu.Sub>
    )
  }
  return (
    <Menu position="bottom-end" width={540} withinPortal shadow="md">
      <Menu.Target>
        <ActionIcon variant="subtle" size={42} radius="md" aria-label="More actions" style={triggerStyle}>
          <IconDotsVertical size={19} />
        </ActionIcon>
      </Menu.Target>
      <Menu.Dropdown>
        <SimpleGrid cols={2} spacing={4}>
          <div style={{ borderRight: '1px solid var(--mantine-color-default-border)', paddingRight: 4 }}>
            <Menu.Label>Series</Menu.Label>
            {item('Refresh chapters', <IconRefresh size={16} />)}
            {item('Refresh metadata and poster', <IconPhoto size={16} />)}
            {item('Edit metadata', <IconPencil size={16} />)}
            <Menu.Label>Files</Menu.Label>
            {item('Rescan files', <IconScan size={16} />)}
            {item('Move to another root folder', <IconFolderSymlink size={16} />)}
            {item('Rename files', <IconFileText size={16} />)}
            {item('Undo conversion...', <IconArrowBackUp size={16} />)}
            <Menu.Label>This device</Menu.Label>
            {item('Save unread chapters for offline', <IconDeviceMobileDown size={16} />)}
            {item('Remove offline copies', <IconDeviceMobileOff size={16} />)}
          </div>
          <div>
            <Menu.Label>Automation</Menu.Label>
            {sub('monitor')}
            {sub('notify')}
            {sub('incognito')}
            <Menu.Label>Quality and storage</Menu.Label>
            {sub('profile')}
            {sub('conversion')}
            {sub('cleanup')}
            {item('Scan for upgrades', <IconRefresh size={16} />)}
            {item('Search volume releases', <IconSearch size={16} />)}
          </div>
        </SimpleGrid>
        <Menu.Divider />
        <Group grow gap={4} wrap="nowrap">
          {item('Archive', <IconArchive size={16} />)}
          {item('Remove from library', <IconTrash size={16} />, 'var(--danger)')}
        </Group>
      </Menu.Dropdown>
    </Menu>
  )
}

/* ---------------------------------------------------------------------------------------------- */

const meta = {
  title: 'Concepts/Series actions menu',
  tags: ['ai-generated'],
  args: { onAction: fn(), onSetting: fn() },
  decorators: [
    (Story) => (
      <div style={{ display: 'flex', justifyContent: 'flex-end', alignItems: 'flex-start', minHeight: 640 }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<ConceptProps>

export default meta
type Story = StoryObj<typeof meta>

const body = (canvasElement: HTMLElement) => within(canvasElement.ownerDocument.body)

/** Today's menu with every group visible, for comparison. Taller than an 800px window. */
export const Current: Story = {
  render: () => (
    <SeriesActionsMenu
      monitorMode="All"
      incognito="Off"
      notificationMode="Default"
      upgradeProfileId={null}
      upgradeProfiles={upgradeProfiles}
      readFileCleanup="Default"
      readFileCleanupActive={false}
      pageConversion="Default"
      canSetPageConversion
      canEdit
      canCleanUpReadFiles
      canScanUpgrades
      scanningUpgrades={false}
      searchingVolumes={false}
      busy={false}
      canArchive
      archived={false}
      canRemove
      offlineSaved={4}
      onSaveUnreadOffline={fn()}
      onRemoveOffline={fn()}
      onUndoConversion={fn()}
      onRefreshChapters={fn()}
      onRefreshMetadata={fn()}
      onEditMetadata={fn()}
      onRescan={fn()}
      onMove={fn()}
      onRename={fn()}
      onSetMonitor={fn()}
      onSetIncognito={fn()}
      onSetNotify={fn()}
      onSetUpgradeProfile={fn()}
      onSetReadFileCleanup={fn()}
      onSetPageConversion={fn()}
      onScanUpgrades={fn()}
      onSearchVolumes={fn()}
      onArchive={fn()}
      onRestore={fn()}
      onRemove={fn()}
    />
  ),
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'More actions' }))
  },
}

/** A. Actions menu keeps one-shot actions only; settings get their own button and popover. */
export const SplitActionsMenu: Story = {
  render: (args) => <SplitConcept {...args} />,
  play: async ({ args, canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'More actions' }))
    await userEvent.click(await body(canvasElement).findByRole('menuitem', { name: 'Edit metadata' }))
    await waitFor(() => expect(args.onAction).toHaveBeenCalledWith('Edit metadata'))
    await userEvent.click(await canvas.findByRole('button', { name: 'More actions' }))
  },
}

/** A. The settings popover: every value visible at once, the hint under each follows the choice. */
export const SplitSettingsPopover: Story = {
  render: (args) => <SplitConcept {...args} />,
  play: async ({ args, canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Series settings' }))
    const dialog = await body(canvasElement).findByRole('dialog')
    await userEvent.click(await within(dialog).findByText('No scrobble'))
    await waitFor(() => expect(args.onSetting).toHaveBeenCalledWith('incognito', 'ScrobbleOnly'))
    await expect(await within(dialog).findByText('Skips tracker pushes.')).toBeVisible()
  },
}

/** A. A reader without edit rights: the popover shrinks to their own two settings. */
export const SplitSettingsReader: Story = {
  args: { reader: true },
  render: (args) => <SplitConcept {...args} />,
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Series settings' }))
    const dialog = await body(canvasElement).findByRole('dialog')
    await expect(await within(dialog).findByText('Just for you')).toBeVisible()
    await expect(within(dialog).queryByText('For everyone')).toBeNull()
  },
}

/** B. Root list of eleven rows; groups open in place. */
export const DrillDownRoot: Story = {
  render: (args) => <DrillDownConcept {...args} />,
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'More actions' }))
    await expect(await body(canvasElement).findByRole('menuitem', { name: 'Files' })).toBeVisible()
  },
}

/** B. Two steps in: Monitoring and alerts, then Monitor, with its explanation kept. Picking returns to the group. */
export const DrillDownSetting: Story = {
  render: (args) => <DrillDownConcept {...args} />,
  play: async ({ args, canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'More actions' }))
    const page = body(canvasElement)
    const toMonitor = async () => {
      await userEvent.click(await page.findByRole('menuitem', { name: /^Monitoring and alerts/ }))
      await userEvent.click(await page.findByRole('menuitem', { name: /^Monitor(?!ing)/ }))
      await expect(await page.findByText('What happens to chapters released later')).toBeVisible()
    }
    await toMonitor()
    const smart = await page.findByRole('menuitemradio', { name: 'Smart' })
    smart.focus()
    await userEvent.keyboard('{Enter}')
    await waitFor(() => expect(args.onSetting).toHaveBeenCalledWith('monitor', 'Smart'))
    await expect(await page.findByRole('menuitem', { name: /^Monitor(?!ing).*Smart/ })).toBeVisible()
    await userEvent.click(await page.findByRole('menuitem', { name: /^Monitor(?!ing)/ }))
  },
}

/** C. Same items and submenus as today in two columns, about half the height. Desktop only. */
export const WidePanel: Story = {
  render: (args) => <WidePanelConcept {...args} />,
  play: async ({ args, canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'More actions' }))
    await userEvent.click(await body(canvasElement).findByRole('menuitem', { name: 'Rescan files' }))
    await waitFor(() => expect(args.onAction).toHaveBeenCalledWith('Rescan files'))
    await userEvent.click(await canvas.findByRole('button', { name: 'More actions' }))
  },
}
