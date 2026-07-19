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
  IconAlertTriangle,
  IconDownload,
  IconHeartbeat,
} from '@tabler/icons-react'
import { Link, Route, Routes, useLocation } from 'react-router-dom'
import { useAppVersion, useHealth, useQueue, useSetupStatus } from './api/hooks'
import { useLiveEvents } from './api/signalr'
import CommandPalette from './components/CommandPalette'
import SetupWizard from './components/SetupWizard'
import { isQueueActive } from './components/ui/status'
import { ALL_ITEMS, NAV_SECTIONS, isActive, pageTitle } from './nav'
import LibraryPage from './pages/LibraryPage'
import SeriesDetailPage from './pages/SeriesDetailPage'
import AddSeriesPage from './pages/AddSeriesPage'
import ActivityPage from './pages/ActivityPage'
import DiscoverPage from './pages/DiscoverPage'
import ImportPage from './pages/ImportPage'
import ScrobblePage from './pages/ScrobblePage'
import RewindPage from './pages/RewindPage'
import SettingsPage from './pages/SettingsPage'

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

function VersionFooter() {
  const { data: version } = useAppVersion()
  if (!version) return null
  // A -dev / -nightly suffix means the build was not cut from a release tag; flag it so a local or
  // CI-of-main image is never mistaken for a published version.
  const unofficial = /-(dev|nightly)/.test(version)
  return (
    <Tooltip label={unofficial ? 'Unofficial build (not a tagged release)' : `Maki ${version}`} withArrow>
      <Text
        fz={10}
        c="dimmed"
        fw={600}
        px={4}
        tt="uppercase"
        style={{ letterSpacing: '0.08em' }}
      >
        v{version}
      </Text>
    </Tooltip>
  )
}

function App() {
  const location = useLocation()
  const [opened, { toggle, close }] = useDisclosure()
  const { data: setup } = useSetupStatus()
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
            <CommandPalette navItems={ALL_ITEMS} />
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
              Maki
            </Text>
            <Text fz={10} c="dimmed" fw={600} tt="uppercase" style={{ letterSpacing: '0.12em' }}>
              Manga manager
            </Text>
          </div>
        </Group>
        <AppShell.Section grow component={ScrollArea} type="never">
          <NavLinks onNavigate={close} />
        </AppShell.Section>
        <AppShell.Section>
          <VersionFooter />
        </AppShell.Section>
      </AppShell.Navbar>

      <AppShell.Main>
        <Routes>
          <Route path="/" element={<LibraryPage />} />
          <Route path="/series/:id" element={<SeriesDetailPage />} />
          <Route path="/add" element={<AddSeriesPage />} />
          <Route path="/discover/:tab?" element={<DiscoverPage />} />
          <Route path="/import" element={<ImportPage />} />
          <Route path="/activity" element={<ActivityPage />} />
          <Route path="/scrobble" element={<ScrobblePage />} />
          <Route path="/rewind" element={<RewindPage />} />
          <Route path="/settings" element={<SettingsPage />} />
        </Routes>
      </AppShell.Main>

      {setup && !setup.completed && <SetupWizard />}
    </AppShell>
  )
}

/** Small book glyph used inside the gradient brand tile. */
function IconBrandMark() {
  return (
    <svg width="30" height="30" viewBox="0 0 96 96" fill="none" aria-hidden>
      <g stroke="#1a1a1a" strokeWidth="3.5" strokeLinejoin="round">
        <rect x="22" y="20" width="52" height="56" rx="15" fill="#f4ecd8" />
        <path d="M22 35 a15 15 0 0 1 15 -15 h22 a15 15 0 0 1 15 15 v2 h-52 z" fill="#20301f" />
        <path d="M22 61 h52 a15 15 0 0 1 -15 15 h-22 a15 15 0 0 1 -15 -15 z" fill="#20301f" />
      </g>
      <g fill="#1a1a1a">
        <circle cx="38" cy="46" r="4" />
        <circle cx="58" cy="46" r="4" />
      </g>
      <circle cx="39.3" cy="44.6" r="1.3" fill="#fff" />
      <circle cx="59.3" cy="44.6" r="1.3" fill="#fff" />
      <circle cx="31" cy="52" r="2.8" fill="#f7a8bf" />
      <circle cx="65" cy="52" r="2.8" fill="#f7a8bf" />
      <path d="M43 53 q5 4 10 0" fill="none" stroke="#1a1a1a" strokeWidth="2.4" strokeLinecap="round" />
    </svg>
  )
}

export default App
