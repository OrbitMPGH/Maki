import {
  ActionIcon,
  AppShell,
  Badge,
  Burger,
  Group,
  Indicator,
  Popover,
  ScrollArea,
  Stack,
  Text,
  Tooltip,
} from '@mantine/core'
import { useDisclosure } from '@mantine/hooks'
import {
  IconActivity,
  IconAlertTriangle,
  IconDownload,
  IconFolderDown,
  IconHeartbeat,
  IconLibrary,
  IconPlus,
  IconRefreshDot,
  IconSettings,
  IconSparkles,
  type Icon,
} from '@tabler/icons-react'
import { Link, Route, Routes, useLocation } from 'react-router-dom'
import { useHealth, useQueue } from './api/hooks'
import { useLiveEvents } from './api/signalr'
import { isQueueActive } from './components/ui/status'
import LibraryPage from './pages/LibraryPage'
import SeriesDetailPage from './pages/SeriesDetailPage'
import AddSeriesPage from './pages/AddSeriesPage'
import ActivityPage from './pages/ActivityPage'
import DiscoverPage from './pages/DiscoverPage'
import ImportPage from './pages/ImportPage'
import ScrobblePage from './pages/ScrobblePage'
import SettingsPage from './pages/SettingsPage'

interface NavItem {
  label: string
  path: string
  icon: Icon
  end?: boolean
}

const NAV_SECTIONS: { label: string; items: NavItem[] }[] = [
  {
    label: 'Collection',
    items: [
      { label: 'Library', path: '/', icon: IconLibrary, end: true },
      { label: 'Add series', path: '/add', icon: IconPlus },
      { label: 'Discover', path: '/discover', icon: IconSparkles },
      { label: 'Import', path: '/import', icon: IconFolderDown },
    ],
  },
  {
    label: 'Automation',
    items: [
      { label: 'Activity', path: '/activity', icon: IconActivity },
      { label: 'Scrobble', path: '/scrobble', icon: IconRefreshDot },
    ],
  },
  {
    label: 'System',
    items: [{ label: 'Settings', path: '/settings', icon: IconSettings }],
  },
]

const ALL_ITEMS = NAV_SECTIONS.flatMap((s) => s.items)

function isActive(item: NavItem, pathname: string): boolean {
  return item.end ? pathname === item.path : pathname.startsWith(item.path)
}

function pageTitle(pathname: string): string {
  if (pathname.startsWith('/series/')) return 'Series'
  const match = ALL_ITEMS.find((i) => isActive(i, pathname))
  return match?.label ?? 'Mangarr'
}

function NavLinks({ onNavigate }: { onNavigate?: () => void }) {
  const { pathname } = useLocation()
  return (
    <Stack gap="lg">
      {NAV_SECTIONS.map((section) => (
        <Stack key={section.label} gap={4}>
          <Text className="nav-section-label" mb={2}>
            {section.label}
          </Text>
          {section.items.map((item) => (
            <Link
              key={item.path}
              to={item.path}
              className="nav-link"
              data-active={isActive(item, pathname)}
              onClick={onNavigate}
            >
              <item.icon size={18} stroke={1.7} className="nav-icon" />
              {item.label}
            </Link>
          ))}
        </Stack>
      ))}
    </Stack>
  )
}

function HealthButton() {
  const { data: health } = useHealth()
  if (!health || health.length === 0) return null
  const hasError = health.some((h) => h.severity === 'error')
  return (
    <Popover width={340} position="bottom-end" withArrow shadow="md">
      <Popover.Target>
        <Indicator size={16} color={hasError ? 'red' : 'yellow'} label={health.length} withBorder>
          <ActionIcon variant="subtle" color={hasError ? 'red' : 'yellow'} aria-label="Health issues">
            <IconAlertTriangle size={19} />
          </ActionIcon>
        </Indicator>
      </Popover.Target>
      <Popover.Dropdown>
        <Group gap={6} mb="xs">
          <IconHeartbeat size={16} />
          <Text fw={650} size="sm">
            Health
          </Text>
        </Group>
        <Stack gap="xs">
          {health.map((issue, i) => (
            <Group key={i} gap="xs" wrap="nowrap" align="flex-start">
              <Badge
                size="xs"
                color={issue.severity === 'error' ? 'red' : 'yellow'}
                variant="light"
                mt={2}
              >
                {issue.severity}
              </Badge>
              <Text size="xs" c="dimmed">
                {issue.message}
              </Text>
            </Group>
          ))}
        </Stack>
      </Popover.Dropdown>
    </Popover>
  )
}

function ActivityButton() {
  const { data: queue } = useQueue()
  const active = queue?.items.filter((q) => isQueueActive(q.status)).length ?? 0
  return (
    <Tooltip label={active > 0 ? `${active} download(s) in progress` : 'Activity'} withArrow>
      <ActionIcon
        component={Link}
        to="/activity"
        variant="subtle"
        color="gray"
        aria-label="Activity"
        pos="relative"
      >
        <IconDownload size={19} />
        {active > 0 && (
          <Badge
            size="xs"
            variant="filled"
            color="brand"
            // A `circle` badge clips 2+ digit counts against its radius; a pill that grows
            // horizontally (with a floor width so single digits still read as a dot) doesn't.
            style={{
              position: 'absolute',
              top: -6,
              right: -6,
              minWidth: 16,
              padding: '0 4px',
              pointerEvents: 'none',
            }}
            className="tnum"
          >
            {active > 99 ? '99+' : active}
          </Badge>
        )}
      </ActionIcon>
    </Tooltip>
  )
}

function App() {
  const location = useLocation()
  const [opened, { toggle, close }] = useDisclosure()
  useLiveEvents()

  return (
    <AppShell
      header={{ height: 58 }}
      navbar={{ width: 232, breakpoint: 'sm', collapsed: { mobile: !opened } }}
      padding="lg"
    >
      <AppShell.Header className="app-header">
        <Group h="100%" px="md" justify="space-between" wrap="nowrap">
          <Group gap="sm" wrap="nowrap">
            <Burger opened={opened} onClick={toggle} hiddenFrom="sm" size="sm" />
            <Group gap="sm" wrap="nowrap" hiddenFrom="sm">
              <span className="brand-mark">
                <IconBrandMark />
              </span>
            </Group>
            <Text fw={700} fz="lg" visibleFrom="sm" style={{ letterSpacing: '-0.01em' }}>
              {pageTitle(location.pathname)}
            </Text>
          </Group>
          <Group gap="xs" wrap="nowrap">
            <ActivityButton />
            <HealthButton />
          </Group>
        </Group>
      </AppShell.Header>

      <AppShell.Navbar className="app-navbar" p="md">
        <Group gap="sm" mb="xl" px={4} wrap="nowrap">
          <span className="brand-mark">
            <IconBrandMark />
          </span>
          <div>
            <Text fw={800} fz="lg" lh={1} style={{ letterSpacing: '-0.02em' }}>
              Mangarr
            </Text>
            <Text fz={10} c="dimmed" fw={600} tt="uppercase" style={{ letterSpacing: '0.12em' }}>
              Manga manager
            </Text>
          </div>
        </Group>
        <AppShell.Section grow component={ScrollArea} type="never">
          <NavLinks onNavigate={close} />
        </AppShell.Section>
      </AppShell.Navbar>

      <AppShell.Main>
        <Routes>
          <Route path="/" element={<LibraryPage />} />
          <Route path="/series/:id" element={<SeriesDetailPage />} />
          <Route path="/add" element={<AddSeriesPage />} />
          <Route path="/discover" element={<DiscoverPage />} />
          <Route path="/import" element={<ImportPage />} />
          <Route path="/activity" element={<ActivityPage />} />
          <Route path="/scrobble" element={<ScrobblePage />} />
          <Route path="/settings" element={<SettingsPage />} />
        </Routes>
      </AppShell.Main>
    </AppShell>
  )
}

/** Small book glyph used inside the gradient brand tile. */
function IconBrandMark() {
  return (
    <svg width="17" height="17" viewBox="0 0 24 24" fill="none" aria-hidden>
      <path
        d="M4 5.5A1.5 1.5 0 0 1 5.5 4H10a1.5 1.5 0 0 1 1.5 1.5V19a1 1 0 0 0-1-1H5.5A1.5 1.5 0 0 1 4 16.5v-11Z"
        fill="currentColor"
        opacity="0.55"
      />
      <path
        d="M20 5.5A1.5 1.5 0 0 0 18.5 4H14a1.5 1.5 0 0 0-1.5 1.5V19a1 1 0 0 1 1-1h5A1.5 1.5 0 0 0 20 16.5v-11Z"
        fill="currentColor"
      />
    </svg>
  )
}

export default App
