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
import MetadataDumpProgress from './components/MetadataDumpProgress'
import SetupWizard from './components/SetupWizard'
import { UserMenu } from './components/UserMenu'
import UpdateBanner from './components/UpdateBanner'
import LanguageAnnouncementModal from './components/LanguageAnnouncementModal'
import { isQueueActive, needsImportReview } from './components/ui/status'
import { NavHistoryProvider, ScrollMemory } from './lib/navHistory'
import { TipLayer } from './components/ui/TipLayer'
import { EmptyState } from './components/ui/EmptyState'
import { useLanguageSync } from './i18n-context'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { useLingui as useLinguiReact } from '@lingui/react'
import { navSections, isActive, type NavItem } from './nav'
import { ShellTitleProvider, useShellTitleValue } from './lib/shellTitle'
// Home and Library stay eagerly imported: "/" resolves to one of the two on every cold load
// (StartPageRedirect), so splitting them would only add a round trip to the first paint.
import HomePage from './pages/HomePage'
import LibraryPage from './pages/LibraryPage'

// Everything else is reached by a navigation, so it can arrive as its own chunk instead of riding
// in the initial bundle. Stats in particular pulls @mantine/charts and recharts, and Settings and
// Discover are the two largest pages in the app, and none of which someone landing on Home needs.
const SeriesDetailPage = lazy(() => import('./pages/SeriesDetailPage'))
const AddSeriesPage = lazy(() => import('./pages/AddSeriesPage'))
const ShortlistPage = lazy(() => import('./pages/ShortlistPage'))
const CreatorPage = lazy(() => import('./pages/CreatorPage'))
const ActivityPage = lazy(() => import('./pages/ActivityPage'))
const RequestsPage = lazy(() => import('./pages/RequestsPage'))
const DiscoverPage = lazy(() => import('./pages/DiscoverPage'))
const ImportPage = lazy(() => import('./pages/ImportPage'))
const ScrobblePage = lazy(() => import('./pages/ScrobblePage'))
const StatsPage = lazy(() => import('./pages/StatsPage'))
const NotificationsPage = lazy(() => import('./pages/NotificationsPage'))
const SettingsPage = lazy(() => import('./pages/SettingsPage'))
const HealthPage = lazy(() => import('./pages/HealthPage'))
const ReaderPage = lazy(() => import('./pages/reader/ReaderPage'))

function ShellTitle() {
  const title = useShellTitleValue()
  if (!title) return null
  return (
    <Text fw={650} fz="md" visibleFrom="sm" truncate className="app-header-title">
      {title}
    </Text>
  )
}

function NotFoundPage() {
  const { t } = useLingui()
  return (
    <EmptyState
      title={t`Page not found`}
      description={t`Nothing lives at this address. The link may be old or mistyped.`}
      actionLabel={t`Go to start page`}
      actionTo="/"
    />
  )
}

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
  const { _ } = useLinguiReact()
  return (
    <Stack gap="lg">
      {sections.map((section) => (
        <Stack key={section.label.id} gap={4}>
          <Text className="nav-section-label" mb={2}>
            {_(section.label)}
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
                {_(item.label)}
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
  const { t } = useLingui()
  const { data: health } = useHealth()
  if (!health || health.length === 0) return null
  const hasError = health.some((h) => h.severity === 'error')
  return (
    <Popover width={340} position="bottom-end" withArrow shadow="md">
      <Popover.Target>
        <Indicator
          size={16}
          color={hasError ? 'var(--danger)' : 'var(--warn)'}
          label={health.length}
          withBorder
        >
          <ActionIcon
            variant="subtle"
            color={hasError ? 'var(--danger)' : 'var(--warn)'}
            aria-label={t`Health issues`}
          >
            <IconAlertTriangle size={19} />
          </ActionIcon>
        </Indicator>
      </Popover.Target>
      <Popover.Dropdown>
        <Group gap={6} mb="xs">
          <IconHeartbeat size={16} />
          <Text fw={650} size="sm">
            <Trans>Health</Trans>
          </Text>
          <Text component={Link} to="/health" size="sm">
            <Trans>Open Health</Trans>
          </Text>
        </Group>
        <Stack gap="xs">
          {health.map((issue, i) => (
            <Group key={i} gap="xs" wrap="nowrap" align="flex-start">
              <Badge
                size="xs"
                color={issue.severity === 'error' ? 'var(--danger)' : 'var(--warn)'}
                variant="light"
                mt={2}
              >
                {issue.severity}
              </Badge>
              <Text size="xs" c="var(--ink-3)">
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
  const { t } = useLingui()
  const { data: queue } = useQueue()
  const active = queue?.items.filter((q) => isQueueActive(q.status)).length ?? 0
  // A download waiting on an import decision outranks work in progress: progress finishes on its
  // own, this does not, and the count is the only thing telling anyone it is there.
  const review = queue?.items.filter((q) => needsImportReview(q.status)).length ?? 0
  const count = review > 0 ? review : active
  return (
    <Tooltip
      label={
        review > 0 ? (
          <Plural value={review} one="# download waiting for an import decision"
            other="# downloads waiting for an import decision" />
        ) : active > 0 ? (
          <Plural value={active} one="# download in progress" other="# downloads in progress" />
        ) : (
          <Trans>Activity</Trans>
        )
      }
      withArrow
    >
      <ActionIcon
        component={Link}
        to="/activity"
        variant="subtle"
        color="gray"
        aria-label={t`Activity`}
        pos="relative"
        style={{ overflow: 'visible' }}
      >
        <IconDownload size={19} />
        {count > 0 && (
          <Badge
            size="xs"
            variant="filled"
            color={review > 0 ? 'var(--warn)' : 'brand'}
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
            {count > 99 ? '99+' : count}
          </Badge>
        )}
      </ActionIcon>
    </Tooltip>
  )
}

function VersionFooter() {
  const { t } = useLingui()
  const { data: version } = useAppVersion()
  if (!version) return null
  // A -dev / -nightly suffix means the build was not cut from a release tag; flag it so a local or
  // CI-of-main image is never mistaken for a published version.
  const unofficial = /-(dev|nightly)/.test(version)
  return (
    <Tooltip label={unofficial ? t`Unofficial build (not a tagged release)` : `Maki ${version}`} withArrow>
      <Text
        fz={10}
        c="var(--ink-3)"
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
      {/* Outside AuthGate so the stack is recorded on every route, the reader included: it is a
          page you can reach a series from, so it is a page a series has to be able to go back to. */}
      <NavHistoryProvider>
        <ScrollMemory />
        <AuthGate />
      </NavHistoryProvider>
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

function AppShellRoutes() {
  const location = useLocation()
  const navigate = useNavigate()
  const [opened, { toggle, close }] = useDisclosure()
  const { data: setup } = useSetupStatus()
  const { data: metadata } = useMetadataSettings()
  const { data: ui } = useUiSettings()
  const { can } = useAuth()
  useLiveEvents()
  // localStorage decided the first paint; the stored preference is what follows the user here.
  useLanguageSync(ui?.language)

  // Both default to "available" while their settings load, so a tab doesn't flash away and back
  // on every visit. HomePage takes the opposite default for its own data, see the note there.
  const discoverAvailable = metadata ? metadata.useLocalDb && metadata.dumpPresent : true
  const homeEnabled = ui ? ui.homeLayout.enabled : true
  const isAdmin = can('Admin')
  const canAdd = can('AddSeries')
  const sections = navSections({
    isAdmin,
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
    <ShellTitleProvider>
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
            <ShellTitle />
          </Group>
          <Group gap="xs" wrap="nowrap">
            <CommandPalette navItems={allItems} />
            <ActivityButton />
            <NotificationBell />
            {isAdmin && <HealthButton />}
            <UserMenu />
          </Group>
        </Group>
      </AppShell.Header>

      {/* The mobile navbar is a drawer over the page, and Mantine ships no scrim for it: without
          one there is nowhere to tap to dismiss it except the burger. `hiddenFrom` rather than a
          media query so it can never appear over the desktop layout, where the navbar is a
          column and nothing is covered. */}
      {opened && <Box className="nav-scrim" hiddenFrom="sm" onClick={close} />}

      <AppShell.Navbar className="app-navbar" p="md">
        <Group gap="sm" mb="xl" px={4} wrap="nowrap">
          <span className="brand-mark">
            <IconBrandMark />
          </span>
          <div>
            <Text fz="lg" lh={1} className="brand-wordmark">
              Maki
            </Text>
            <Text fz={10} c="var(--ink-3)" fw={600} tt="uppercase" style={{ letterSpacing: '0.12em' }}>
              <Trans>Manga manager</Trans>
            </Text>
          </div>
        </Group>
        <AppShell.Section grow component={ScrollArea} type="never">
          <NavLinks
            sections={sections}
            onNavigate={close}
            badges={{ '/requests': pendingRequests?.count ?? 0 }}
          />
        </AppShell.Section>
        <AppShell.Section>
          <VersionFooter />
        </AppShell.Section>
      </AppShell.Navbar>

      {/* Zeroes the shell padding for the pages whose hero band bleeds to the window edges: the
          series page, Home, and Discover's browse tab. Written as "Discover, but not its other tabs"
          rather than "/discover exactly", because DiscoverPage falls back to the browse tab for any
          unrecognised :tab — a stale /discover/genres link lands on the band and has to bleed like
          the canonical URL does. Recommended, Your Taste and the Queue have no band and keep their
          padding. */}
      <AppShell.Main
        className={
          /^\/series\/\d+(?:\/|$)/.test(location.pathname) ||
          /^\/discover(?!\/(?:recommended|taste|queue)(?:\/|$))/.test(location.pathname)
            ? 'app-main-hero'
            : undefined
        }
      >
        <UpdateBanner />
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
            <Route path="/shortlist" element={<ShortlistPage />} />
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
            <Route path="/health" element={isAdmin ? <HealthPage /> : <Navigate to="/" replace />} />
            <Route path="*" element={<NotFoundPage />} />
          </Routes>
        </Suspense>
      </AppShell.Main>

      {/* Opens itself off the setup flag, latched so a language pick's cache clear can't close it. */}
      {isAdmin && <SetupWizard />}
      {/* Only once the instance is past first-run: the wizard owns the screen while it is up, and
          nobody being handed a brand-new Maki needs to be told what changed in it. */}
      {setup?.completed && <LanguageAnnouncementModal />}
      {/* Owns a toast, not a piece of the page, so it sits outside Main and renders nothing. */}
      {isAdmin && <MetadataDumpProgress />}
      <TipLayer />
    </AppShell>
    </ShellTitleProvider>
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
