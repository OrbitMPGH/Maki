import {
  ActionIcon,
  AppShell,
  Badge,
  Box,
  Burger,
  Center,
  Group,
  Indicator,
  Loader,
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
import { lazy, Suspense, useEffect } from 'react'
import { Link, Navigate, Route, Routes, useLocation, useNavigate } from 'react-router-dom'
import {
  useAppVersion,
  useHealth,
  useMetadataSettings,
  useQueue,
  useSetupStatus,
  useUiSettings,
} from './api/hooks'
import { usePendingRequestCount } from './api/requests'
import { useLiveEvents } from './api/signalr'
import { AuthProvider, useAuth } from './auth/AuthProvider'
import { LoginPage } from './pages/LoginPage'
import { SetupAccountPage } from './pages/SetupAccountPage'
import CommandPalette from './components/CommandPalette'
import { IconBrandMark } from './components/IconBrandMark'
import { NotificationBell } from './components/NotificationBell'
import SetupWizard from './components/SetupWizard'
import { UserMenu } from './components/UserMenu'
import UpdateBanner from './components/UpdateBanner'
import { isQueueActive } from './components/ui/status'
import { TipLayer } from './components/ui/TipLayer'
import { navSections, isActive, pageTitle, type NavItem } from './nav'
// Home and Library stay eagerly imported: "/" resolves to one of the two on every cold load
// (StartPageRedirect), so splitting them would only add a round trip to the first paint.
import HomePage from './pages/HomePage'
import LibraryPage from './pages/LibraryPage'

// Everything else is reached by a navigation, so it can arrive as its own chunk instead of riding
// in the initial bundle. Stats in particular pulls @mantine/charts and recharts, and Settings and
// Discover are the two largest pages in the app, and none of which someone landing on Home needs.
const SeriesDetailPage = lazy(() => import('./pages/SeriesDetailPage'))
const AddSeriesPage = lazy(() => import('./pages/AddSeriesPage'))
const CreatorPage = lazy(() => import('./pages/CreatorPage'))
const ActivityPage = lazy(() => import('./pages/ActivityPage'))
const RequestsPage = lazy(() => import('./pages/RequestsPage'))
const DiscoverPage = lazy(() => import('./pages/DiscoverPage'))
const ImportPage = lazy(() => import('./pages/ImportPage'))
const ScrobblePage = lazy(() => import('./pages/ScrobblePage'))
const StatsPage = lazy(() => import('./pages/StatsPage'))
const NotificationsPage = lazy(() => import('./pages/NotificationsPage'))
const SettingsPage = lazy(() => import('./pages/SettingsPage'))
const ReaderPage = lazy(() => import('./pages/reader/ReaderPage'))

/** Shared placeholder while a route chunk is in flight. Matches StartPageRedirect's loader. */
function RouteFallback() {
  return (
    <Center py={80}>
      <Loader />
    </Center>
  )
}

function NavLinks({
  sections,
  onNavigate,
  badges,
}: {
  sections: ReturnType<typeof navSections>
  onNavigate?: () => void
  /** Path → count. A zero or missing entry draws no badge. */
  badges?: Record<string, number>
}) {
  const { pathname } = useLocation()
  return (
    <Stack gap="lg">
      {sections.map((section) => (
        <Stack key={section.label} gap={4}>
          <Text className="nav-section-label" mb={2}>
            {section.label}
          </Text>
          {section.items.map((item) => {
            const count = badges?.[item.path] ?? 0
            return (
              <Link
                key={item.path}
                to={item.path}
                className="nav-link"
                data-active={isActive(item, pathname)}
                onClick={onNavigate}
              >
                <item.icon size={18} stroke={1.7} className="nav-icon" />
                {item.label}
                {count > 0 && (
                  <Badge size="xs" variant="filled" color="brand" ml="auto" className="tnum">
                    {count > 99 ? '99+' : count}
                  </Badge>
                )}
              </Link>
            )
          })}
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
        style={{ overflow: 'visible' }}
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
  return (
    <AuthProvider>
      <AuthGate />
    </AuthProvider>
  )
}

/**
 * Decides between first-run setup, the login screen, and the app.
 *
 * Everything below this point can assume a signed-in user, which is why no page has to handle a
 * missing identity. The server does not rely on that for a moment (every endpoint authorizes
 * independently), but it keeps the UI from rendering half a library while a 401 resolves.
 */
function AuthGate() {
  const location = useLocation()
  const { me, loading, setupNeeded } = useAuth()

  if (loading) {
    return <RouteFallback />
  }

  if (setupNeeded) {
    return <SetupAccountPage />
  }

  if (!me) {
    return <LoginPage />
  }

  // The reader owns the whole viewport, so it renders outside the AppShell rather than inside
  // <AppShell.Main>. Kept out of NAV_SECTIONS too, which also keeps it out of the ⌘K palette.
  if (location.pathname.startsWith('/read/')) {
    return (
      <Suspense fallback={<RouteFallback />}>
        <Routes>
          <Route path="/read/:chapterId" element={<ReaderPage />} />
        </Routes>
      </Suspense>
    )
  }

  return <AppShellRoutes />
}

/**
 * Pages that open with a full-bleed backdrop band, so the header sits over the art with no surface
 * of its own. Both are single-entity pages; see the hero/working split in
 * .claude/rules/design-system.md.
 */
const HERO_ROUTES = [/^\/series\/\d+/, /^\/creator\//]
const isHeroRoute = (pathname: string) => HERO_ROUTES.some((r) => r.test(pathname))

function AppShellRoutes() {
  const location = useLocation()
  const navigate = useNavigate()
  const [opened, { toggle, close }] = useDisclosure()
  const { data: setup } = useSetupStatus()
  const { data: metadata } = useMetadataSettings()
  const { data: ui } = useUiSettings()
  const { can } = useAuth()
  useLiveEvents()

  // Both default to "available" while their settings load, so a tab doesn't flash away and back
  // on every visit. HomePage takes the opposite default for its own data, see the note there.
  const discoverAvailable = metadata ? metadata.useLocalDb && metadata.dumpPresent : true
  const homeEnabled = ui ? ui.homeLayout.enabled : true
  const isAdmin = can('Admin')
  const canAdd = can('AddSeries')
  const sections = navSections({
    discoverAvailable,
    homeEnabled,
    canAdd,
    // An admin works the queue; anyone who has to ask for a series or a download wants to see what
    // happened to what they asked for. Someone holding both permissions never files one.
    requestsVisible: isAdmin || !canAdd || !can('DownloadChapters'),
  })
  const allItems: NavItem[] = sections.flatMap((s) => s.items)
  const { data: pendingRequests } = usePendingRequestCount(isAdmin)

  useEffect(() => {
    // Send anyone sitting on a page that has just become unavailable back through "/", which
    // resolves to whatever their start page is now allowed to be.
    const stranded =
      (!discoverAvailable && location.pathname.startsWith('/discover')) ||
      (!homeEnabled && location.pathname.startsWith('/home'))
    if (stranded) {
      navigate('/', { replace: true })
    }
  }, [discoverAvailable, homeEnabled, location.pathname, navigate])

  return (
    <AppShell
      header={{ height: 58 }}
      navbar={{ width: 212, breakpoint: 'sm', collapsed: { mobile: !opened } }}
      padding="lg"
    >
      <AppShell.Header className="app-header" data-transparent={isHeroRoute(location.pathname)}>
        <Group h="100%" px="md" justify="space-between" wrap="nowrap">
          <Group gap="sm" wrap="nowrap">
            <Burger opened={opened} onClick={toggle} hiddenFrom="sm" size="sm" />
            <Group gap="sm" wrap="nowrap" hiddenFrom="sm">
              <span className="brand-mark">
                <IconBrandMark />
              </span>
            </Group>
            {!isHeroRoute(location.pathname) && (
              <Text fw={700} fz="lg" visibleFrom="sm" style={{ letterSpacing: '-0.01em' }}>
                {pageTitle(location.pathname)}
              </Text>
            )}
          </Group>
          <Group gap="xs" wrap="nowrap">
            <CommandPalette navItems={allItems} />
            <ActivityButton />
            <NotificationBell />
            <HealthButton />
          </Group>
        </Group>
      </AppShell.Header>

      {/* The mobile navbar is a drawer over the page, and Mantine ships no scrim for it: without
          one there is nowhere to tap to dismiss it except the burger. `hiddenFrom` rather than a
          media query so it can never appear over the desktop layout, where the navbar is a
          column and nothing is covered. */}
      {opened && <Box className="nav-scrim" hiddenFrom="sm" onClick={close} />}

      <AppShell.Navbar className="app-navbar" p="md">
        <Group gap={10} mb="lg" px={4} wrap="nowrap">
          <span className="brand-mark">
            <IconBrandMark />
          </span>
          <Text fw={700} fz={19} lh={1} style={{ letterSpacing: '-0.02em' }}>
            Maki
          </Text>
        </Group>
        <AppShell.Section grow component={ScrollArea} type="never">
          <NavLinks
            sections={sections}
            onNavigate={close}
            badges={{ '/requests': pendingRequests?.count ?? 0 }}
          />
        </AppShell.Section>
        <AppShell.Section>
          <Stack gap={6} pt="sm" style={{ borderTop: '1px solid var(--border)' }}>
            <UserMenu full />
            <VersionFooter />
          </Stack>
        </AppShell.Section>
      </AppShell.Navbar>

      <AppShell.Main className={isHeroRoute(location.pathname) ? 'app-main-hero' : undefined}>
        {/* Wrapped so a hero route can give it back the header clearance the column just dropped.
            The banner renders nothing at all when there is no update, and an empty div is not a
            layout. */}
        <div className="update-banner-slot">
          <UpdateBanner />
        </div>
        {/* One boundary around the whole switch rather than one per lazy route: only a single
            route is ever resolving, and a shared fallback keeps the loader identical everywhere. */}
        <Suspense fallback={<RouteFallback />}>
          <Routes>
            <Route
              path="/"
              element={
                <StartPageRedirect
                  discoverAvailable={discoverAvailable}
                  discoverKnown={metadata !== undefined}
                />
              }
            />
            {/* Kept mounted while Home is off so a bookmark lands somewhere real rather than on a
                blank router miss; the effect above bounces it out through "/". */}
            <Route
              path="/home"
              element={homeEnabled ? <HomePage /> : <Navigate to="/library" replace />}
            />
            <Route path="/library" element={<LibraryPage />} />
            <Route path="/series/:id" element={<SeriesDetailPage />} />
            <Route path="/add" element={<AddSeriesPage />} />
            <Route path="/creator/:name" element={<CreatorPage />} />
            <Route path="/discover/:tab?" element={<DiscoverPage />} />
            <Route path="/import" element={<ImportPage />} />
            <Route path="/activity" element={<ActivityPage />} />
            <Route path="/requests" element={<RequestsPage />} />
            <Route path="/scrobble" element={<ScrobblePage />} />
            <Route path="/stats" element={<StatsPage />} />
            <Route path="/notifications" element={<NotificationsPage />} />
            {/* The page was called Rewind until the all-time tab arrived. Bookmarks and any link
                already out there keep working. */}
            <Route path="/rewind" element={<Navigate replace to="/stats" />} />
            <Route path="/settings" element={<SettingsPage />} />
          </Routes>
        </Suspense>
      </AppShell.Main>

      {setup && !setup.completed && <SetupWizard />}
      <TipLayer />
    </AppShell>
  )
}

/**
 * Resolves "/" to the configured start page with a *replacing* navigation, so "/" stays a valid
 * bookmark, the back button is unaffected, and the nav highlight and page title work off the real
 * path with no special cases.
 *
 * Renders a loader rather than a default page while the setting is in flight: rendering Home and
 * swapping it out is a visible flash of the wrong page on every cold load.
 *
 * Both fallbacks are load-bearing, not politeness. AppShellRoutes bounces /discover → / when the
 * local MangaBaka database is missing and /home → / when Home is switched off, so a "/" that
 * redirected to either unconditionally would ping-pong forever. Waiting for `discoverKnown` avoids
 * a one-frame trip through that guard, since metadata settings default to "available" while they
 * load; the Home flag needs no equivalent because it arrives with the start page itself.
 */
function StartPageRedirect({
  discoverAvailable,
  discoverKnown,
}: {
  discoverAvailable: boolean
  discoverKnown: boolean
}) {
  const { data: ui, isPending } = useUiSettings()

  if (isPending || (ui?.startPage === 'discover' && !discoverKnown)) {
    return (
      <Center py={80}>
        <Loader />
      </Center>
    )
  }

  // Home is the last resort only while it exists; with it off, the library is.
  const homeEnabled = ui?.homeLayout.enabled ?? true
  const target =
    ui?.startPage === 'discover' && discoverAvailable
      ? '/discover'
      : ui?.startPage === 'library'
        ? '/library'
        : homeEnabled
          ? '/home'
          : '/library'

  return <Navigate to={target} replace />
}

export default App
