import {
  ActionIcon,
  AppShell,
  Box,
  Burger,
  Center,
  Group,
  Loader,
  ScrollArea,
  Stack,
  Text,
  Tooltip,
} from '@mantine/core'
import { useDisclosure, useMediaQuery } from '@mantine/hooks'
import {
  IconArrowLeft,
  IconLayoutSidebarLeftCollapse,
  IconLayoutSidebarLeftExpand,
} from '@tabler/icons-react'
import { Suspense, useEffect, useRef, useState, type ReactNode } from 'react'
import { Link, Navigate, Route, Routes, useLocation, useNavigate } from 'react-router-dom'
import {
  useHealth,
  useMetadataSettings,
  usePrefetchSeriesOnHover,
  useSetupStatus,
  useUiSettings,
} from './api/hooks'
import { usePendingRequestCount } from './api/requests'
import { useLiveEvents } from './api/signalr'
import { AuthProvider, useAuth } from './auth/AuthProvider'
import { LoginPage } from './pages/LoginPage'
import { OfflineStartPage } from './auth/OfflineStartPage'
import { OfflineBanner } from './components/OfflineBanner'
import { OfflineSync } from './pwa/OfflineSync'
import { SetupAccountPage } from './pages/SetupAccountPage'
import MetadataGatePage from './pages/MetadataGatePage'
import { CommandPaletteModal, CommandPaletteTrigger } from './components/CommandPalette'
import { IconBrandMark } from './components/IconBrandMark'
import { NotificationBell } from './components/NotificationBell'
import { ActivityButton } from './components/ActivityButton'
import MetadataDumpProgress from './components/MetadataDumpProgress'
import SetupWizard from './components/SetupWizard'
import SidebarFooter from './components/layout/SidebarFooter'
import LanguageAnnouncementModal from './components/LanguageAnnouncementModal'
import AppearanceAnnouncementModal from './components/AppearanceAnnouncementModal'
import { NavHistoryProvider, ScrollMemory } from './lib/navHistory'
import { TipLayer } from './components/ui/TipLayer'
import { EmptyState } from './components/ui/EmptyState'
import { RouteErrorBoundary } from './components/RouteErrorBoundary'
import { useLanguageSync } from './i18n-context'
import { Trans, useLingui } from '@lingui/react/macro'
import { useLingui as useLinguiReact } from '@lingui/react'
import { navSections, isActive, type NavItem } from './nav'
import { DocumentTitle, DocumentTitleProvider } from './lib/documentTitle'
import { lazyRoute, preloadRoutes } from './lib/routePreload'
// Home and Library stay eagerly imported: "/" resolves to one of the two on every cold load
// (StartPageRedirect), so splitting them would only add a round trip to the first paint.
import HomePage from './pages/HomePage'
import LibraryPage from './pages/LibraryPage'

// Everything else is reached by a navigation, so it can arrive as its own chunk instead of riding
// in the initial bundle. Stats in particular pulls @mantine/charts and recharts, and Settings and
// Discover are the two largest pages in the app, and none of which someone landing on Home needs.
const SeriesDetailPage = lazyRoute(() => import('./pages/SeriesDetailPage'))
const AddSeriesPage = lazyRoute(() => import('./pages/AddSeriesPage'))
const CreatorPage = lazyRoute(() => import('./pages/CreatorPage'))
const ActivityPage = lazyRoute(() => import('./pages/ActivityPage'))
const RequestsPage = lazyRoute(() => import('./pages/RequestsPage'))
const DiscoverPage = lazyRoute(() => import('./pages/DiscoverPage'))
const ImportPage = lazyRoute(() => import('./pages/ImportPage'))
const ScrobblePage = lazyRoute(() => import('./pages/ScrobblePage'))
const StatsPage = lazyRoute(() => import('./pages/StatsPage'))
const NotificationsPage = lazyRoute(() => import('./pages/NotificationsPage'))
const SettingsPage = lazyRoute(() => import('./pages/SettingsPage'))
const HealthPage = lazyRoute(() => import('./pages/HealthPage'))
const ReaderPage = lazyRoute(() => import('./pages/reader/ReaderPage'))

const NAV_COLLAPSED_KEY = 'sidebar-collapsed'

function readNavCollapsed(): boolean {
  try {
    return localStorage.getItem(NAV_COLLAPSED_KEY) === '1'
  } catch {
    return false
  }
}

function NotFoundPage() {
  const { t } = useLingui()
  return (
    <EmptyState
      art="missing"
      headingOrder={1}
      title={t`Page not found`}
      description={t`Nothing lives at this address. The link may be old or mistyped.`}
      actionLabel={t`Go to start page`}
      actionTo="/"
      actionIcon={<IconArrowLeft size={16} />}
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
  badges?: Record<string, { count: number; tone?: 'warn' | 'danger' }>
}) {
  const { pathname } = useLocation()
  const { _ } = useLinguiReact()
  return (
    <Stack gap={22}>
      {sections.map((section) => (
        <Stack key={section.label.id} gap={2}>
          <Text className="nav-section-label" mb={2}>
            {_(section.label)}
          </Text>
          {section.items.map((item) => {
            const count = badges?.[item.path]?.count ?? 0
            const tone = badges?.[item.path]?.tone
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
                  <span className="nav-count tnum" data-tone={tone}>
                    {count > 99 ? '99+' : count}
                  </span>
                )}
              </Link>
            )
          })}
        </Stack>
      ))}
    </Stack>
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
  const { me, loading, setupNeeded, offline } = useAuth()

  if (loading) {
    return <RouteFallback />
  }

  if (setupNeeded) {
    return <SetupAccountPage />
  }

  if (!me) {
    return offline ? <OfflineStartPage /> : <LoginPage />
  }

  // The reader owns the whole viewport, so it renders outside the AppShell rather than inside
  // <AppShell.Main>. Kept out of NAV_SECTIONS too, which also keeps it out of the ⌘K palette.
  const inReader = location.pathname.startsWith('/read/')

  // Mounted above the reader/shell split so entering the reader neither drops the live
  // subscriptions nor leaves it without inbox toasts and cache updates.
  return (
    <DocumentTitleProvider>
      <LiveEvents />
      <OfflineSync />
      <DocumentTitle />
      <MetadataGate>
        {inReader ? (
          <RouteErrorBoundary>
            <Suspense fallback={<RouteFallback />}>
              <Routes>
                <Route path="/read/:chapterId" element={<ReaderPage />} />
              </Routes>
            </Suspense>
          </RouteErrorBoundary>
        ) : (
          <AppShellRoutes />
        )}
      </MetadataGate>
    </DocumentTitleProvider>
  )
}

/** What an admin can still open while the dump is missing: enough to find out why and fix it. */
function openToAdminsWithoutDump(pathname: string) {
  return ['/settings', '/health'].some((p) => pathname === p || pathname.startsWith(`${p}/`))
}

/**
 * Holds the app back until the local MangaBaka database is installed, since nothing falls back to
 * the API any more. The reader stays open to everyone, since chapters on disk need no metadata, and
 * Settings and Health stay open to admins. Everything else, "/" included, lands on the gate page
 * rather than redirecting, so StartPageRedirect cannot loop. An admin still in the setup guide is
 * let through: the guide owns the screen, shows the download, and its Finish waits for it. A failed
 * or loading query lets the app through like the other shell flags do, so an offline start still
 * reaches saved chapters.
 */
function MetadataGate({ children }: { children: ReactNode }) {
  const { pathname } = useLocation()
  const { can } = useAuth()
  const isAdmin = can('Admin')
  const { data: metadata } = useMetadataSettings()
  const { data: setup } = useSetupStatus()

  if (metadata?.dumpPresent !== false) return children
  if (pathname.startsWith('/read/')) return children
  if (isAdmin && setup === undefined) return <RouteFallback />
  if (isAdmin && (!setup?.completed || openToAdminsWithoutDump(pathname))) return children
  return <MetadataGatePage />
}

function LiveEvents() {
  useLiveEvents()
  return null
}

function AppShellRoutes() {
  const location = useLocation()
  const navigate = useNavigate()
  usePrefetchSeriesOnHover()
  useEffect(() => preloadRoutes(), [])
  const [pinned, { open: openPinned, close: closePinned }] = useDisclosure()
  // A mouse resting on the burger opens the drawer without pinning it, and leaving the burger and
  // the drawer closes it again. Touch and keyboard never peek: a tap fires pointerenter too, and
  // would open the drawer only for the click that follows to close it.
  const [peeking, setPeeking] = useState(false)
  const peekTimer = useRef(0)
  // How the drawer last opened, which picks its transition: a peek fades in where it stands, a
  // press slides the whole drawer in.
  const [reveal, setReveal] = useState<'hover' | 'press'>('press')
  const opened = pinned || peeking
  const close = () => {
    window.clearTimeout(peekTimer.current)
    setPeeking(false)
    closePinned()
  }
  // A mouse always enters the burger before it clicks it, so the click lands on a drawer the hover
  // already opened. Closing it then would flash the drawer open and shut; the click pins it instead,
  // and a second click closes it.
  const toggle = () => {
    if (pinned) close()
    else {
      if (!peeking) setReveal('press')
      close()
      openPinned()
    }
  }
  const peekEnter = (e: React.PointerEvent) => {
    if (e.pointerType !== 'mouse') return
    window.clearTimeout(peekTimer.current)
    if (!opened) setReveal('hover')
    setPeeking(true)
  }
  // The delay carries the pointer across the gap between the burger and the drawer below it. The
  // drawer's user and help menus open in portals outside it, so the peek holds while one is open.
  const endPeek = () => {
    if (navbarRef.current?.querySelector('[aria-expanded="true"]')) {
      peekTimer.current = window.setTimeout(endPeek, 250)
    } else setPeeking(false)
  }
  const peekLeave = (e: React.PointerEvent) => {
    if (e.pointerType !== 'mouse') return
    window.clearTimeout(peekTimer.current)
    peekTimer.current = window.setTimeout(endPeek, 250)
  }
  useEffect(() => () => window.clearTimeout(peekTimer.current), [])
  // Per device rather than per account: the same person wants the column on a desktop monitor and
  // the room on a tablet.
  const [navCollapsed, setNavCollapsed] = useState(readNavCollapsed)
  // The page's padding animates only while the sidebar folds or unfolds; see `[data-nav-moving]`.
  const [navMoving, setNavMoving] = useState(false)
  const navMovingTimer = useRef(0)
  useEffect(() => () => window.clearTimeout(navMovingTimer.current), [])
  const toggleNavCollapsed = () => {
    const next = !navCollapsed
    setNavCollapsed(next)
    setNavMoving(true)
    window.clearTimeout(navMovingTimer.current)
    navMovingTimer.current = window.setTimeout(() => setNavMoving(false), 400)
    close()
    try {
      if (next) localStorage.setItem(NAV_COLLAPSED_KEY, '1')
      else localStorage.removeItem(NAV_COLLAPSED_KEY)
    } catch {
      /* private mode: collapsed for this visit only */
    }
  }
  const phone = useMediaQuery('(max-width: 47.9375em)', false, { getInitialValueInEffect: false })
  // The navbar is a column on desktop and a drawer on phones and when collapsed; only the drawer
  // can be closed, and a closed one is only translated off-screen, so it has to be taken out of
  // the tab order explicitly.
  const drawerClosed = (navCollapsed || phone) && !opened
  const navbarRef = useRef<HTMLDivElement>(null)
  const burgerRef = useRef<HTMLButtonElement>(null)
  const { data: setup } = useSetupStatus()
  const { data: metadata, isError: metadataFailed } = useMetadataSettings()
  const { data: ui } = useUiSettings()
  const { can, singleUser } = useAuth()
  const { t } = useLingui()
  // localStorage decided the first paint; the stored preference is what follows the user here.
  useLanguageSync(ui?.language)

  // Both default to "available" while their settings load, so a tab doesn't flash away and back
  // on every visit. HomePage takes the opposite default for its own data, see the note there.
  // A failed settings request counts as unavailable, so the start page falls through to Home or the
  // library instead of waiting on an answer that is not coming.
  const discoverAvailable = metadata ? metadata.dumpPresent : !metadataFailed
  const homeEnabled = ui ? ui.homeLayout.enabled : true
  const isAdmin = can('Admin')
  const canAdd = can('AddSeries')
  const canImport = can('ImportLibrary')
  const canTrack = can('UseTrackers')
  const sections = navSections({
    isAdmin,
    discoverAvailable,
    homeEnabled,
    canAdd,
    canImport,
    canTrack,
    // An admin works the queue; anyone who has to ask for a series or a download wants to see what
    // happened to what they asked for. Someone holding both permissions never files one.
    requestsVisible: !singleUser && (isAdmin || !canAdd || !can('DownloadChapters')),
  })
  const allItems: NavItem[] = sections.flatMap((s) => s.items)
  // The places someone reads from, in the collapsed header, so the drawer is only for the rest.
  const quickItems = allItems.filter((i) => ['/home', '/library', '/discover'].includes(i.path))
  const { _ } = useLinguiReact()
  const { data: pendingRequests } = usePendingRequestCount(isAdmin && !singleUser)
  const { data: health } = useHealth(isAdmin)
  const healthIssues = health?.length ?? 0
  const healthTone = health?.some((h) => h.severity === 'error') ? 'danger' : 'warn'
  const [paletteOpened, { open: openPalette, close: closePalette }] = useDisclosure(false)

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

  // Focus moves in only for a drawer someone asked for; a peek under a passing mouse must not take it.
  useEffect(() => {
    if (pinned) navbarRef.current?.querySelector<HTMLElement>('a.nav-link[href]')?.focus()
  }, [pinned])

  useEffect(() => {
    if (!opened) return
    const navbar = navbarRef.current
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape' || e.defaultPrevented) return
      // Focus elsewhere (a popover, a modal, a field in the page) means Escape is meant for that.
      const active = document.activeElement
      const inDrawer = !!active && (navbar?.contains(active) || burgerRef.current?.contains(active))
      if (!inDrawer && active && active !== document.body) return
      window.clearTimeout(peekTimer.current)
      setPeeking(false)
      closePinned()
      if (navbar?.contains(document.activeElement)) burgerRef.current?.focus()
    }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [opened, closePinned])

  return (
    <AppShell
      header={{ height: navCollapsed ? 58 : { base: 58, sm: 0 } }}
      // Collapsed means the phone layout at every width: a breakpoint no screen reaches keeps
      // Mantine in its mobile mode, where the navbar is an overlay and the page keeps its width.
      navbar={{ width: 232, breakpoint: navCollapsed ? 100000 : 'sm', collapsed: { mobile: !opened } }}
      padding="lg"
      className={navCollapsed ? 'nav-collapsed' : undefined}
      data-nav-moving={navMoving || undefined}
    >
      {/* Focuses rather than following the hash, which the router would keep in the URL. */}
      <a
        href="#main"
        className="skip-link"
        onClick={(e) => {
          e.preventDefault()
          document.getElementById('main')?.focus()
        }}
      >
        <Trans>Skip to content</Trans>
      </a>
      {/* Phone, or a collapsed sidebar. Otherwise the sidebar carries search, downloads and the
          bell, so the page starts at the top edge of the window. */}
      <AppShell.Header className="app-header" hiddenFrom={navCollapsed ? undefined : 'sm'}>
        <Group h="100%" className="app-header-row" justify="space-between" wrap="nowrap">
          <Group gap="sm" wrap="nowrap">
            <Burger
              ref={burgerRef}
              opened={opened}
              onClick={toggle}
              onPointerEnter={peekEnter}
              onPointerLeave={peekLeave}
              size="sm"
              aria-label={t`Menu`}
              aria-expanded={opened}
              aria-controls="app-navbar"
            />
            <span className="brand-mark" role="img" aria-label={t`Manga manager`} title={t`Manga manager`}>
              <IconBrandMark />
            </span>
            {navCollapsed && (
              <Group component="nav" gap={4} wrap="nowrap" visibleFrom="sm" aria-label={t`Quick links`}>
                {quickItems.map((item) => (
                  <Link
                    key={item.path}
                    to={item.path}
                    className="app-header-link"
                    data-active={isActive(item, location.pathname)}
                  >
                    <item.icon size={18} stroke={1.7} />
                    {_(item.label)}
                  </Link>
                ))}
              </Group>
            )}
          </Group>
          <Group gap={4} wrap="nowrap" className="app-header-actions">
            {/* A tablet has the room for the full field; a phone keeps the icon. */}
            {navCollapsed && (
              <Box visibleFrom="sm" className="app-header-search">
                <CommandPaletteTrigger onOpen={openPalette} />
              </Box>
            )}
            <Box hiddenFrom={navCollapsed ? 'sm' : undefined}>
              <CommandPaletteTrigger onOpen={openPalette} variant="icon" />
            </Box>
            <ActivityButton />
            <NotificationBell />
          </Group>
        </Group>
      </AppShell.Header>

      {/* The mobile navbar is a drawer over the page, and Mantine ships no scrim for it: without
          one there is nowhere to tap to dismiss it except the burger. `hiddenFrom` rather than a
          media query so it can never appear over the desktop layout, where the navbar is a
          column and nothing is covered. */}
      {pinned && (
        <Box className="nav-scrim" hiddenFrom={navCollapsed ? undefined : 'sm'} onClick={close} />
      )}

      <AppShell.Navbar
        ref={navbarRef}
        id="app-navbar"
        className="app-navbar"
        inert={drawerClosed}
        data-reveal={navCollapsed || phone ? reveal : undefined}
        data-open={opened || undefined}
        onPointerEnter={peeking ? peekEnter : undefined}
        onPointerLeave={peeking ? peekLeave : undefined}
      >
        <Group gap={10} mb={14} px={4} wrap="nowrap">
          <span className="brand-mark" role="img" aria-label={t`Manga manager`} title={t`Manga manager`}>
            <IconBrandMark />
          </span>
          <Text fz="var(--type-section)" lh={1} c="var(--ink-hi)" className="brand-wordmark">
            Maki
          </Text>
          <Tooltip label={navCollapsed ? t`Keep sidebar open` : t`Collapse sidebar`} withArrow>
            <ActionIcon
              visibleFrom="sm"
              variant="subtle"
              color="var(--neutral)"
              className="nav-collapse-toggle"
              onClick={toggleNavCollapsed}
              aria-label={navCollapsed ? t`Keep sidebar open` : t`Collapse sidebar`}
            >
              {navCollapsed ? <IconLayoutSidebarLeftExpand size={19} /> : <IconLayoutSidebarLeftCollapse size={19} />}
            </ActionIcon>
          </Tooltip>
        </Group>
        {!navCollapsed && (
          <Box visibleFrom="sm">
            <CommandPaletteTrigger onOpen={openPalette} />
          </Box>
        )}
        <AppShell.Section grow component={ScrollArea} type="never" mt={14}>
          <NavLinks
            sections={sections}
            onNavigate={close}
            badges={{
              '/requests': { count: pendingRequests?.count ?? 0 },
              '/health': { count: healthIssues, tone: healthTone },
            }}
          />
        </AppShell.Section>
        <AppShell.Section>
          <SidebarFooter
            onNavigate={close}
            actions={
              navCollapsed ? undefined : (
                <Group gap={2} wrap="nowrap" visibleFrom="sm">
                  <ActivityButton position="top-start" />
                  <NotificationBell position="top-start" />
                </Group>
              )
            }
          />
        </AppShell.Section>
      </AppShell.Navbar>

      {/* Zeroes the shell padding on the series page, whose hero band bleeds to the window edges. */}
      <AppShell.Main
        className={/^\/series\/\d+(?:\/|$)/.test(location.pathname) ? 'app-main-hero' : undefined}
        id="main"
        tabIndex={-1}
      >
        <OfflineBanner />
        {/* One boundary around the whole switch rather than one per lazy route: only a single
            route is ever resolving, and a shared fallback keeps the loader identical everywhere.
            The error boundary sits inside the shell on purpose, so a page that throws leaves the
            nav and header standing. */}
        <RouteErrorBoundary>
        <Suspense fallback={<RouteFallback />}>
          <Routes>
            <Route
              path="/"
              element={
                <StartPageRedirect
                  discoverAvailable={discoverAvailable}
                  discoverKnown={metadata !== undefined || metadataFailed}
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
            <Route
              path="/import"
              element={canImport ? <ImportPage /> : <Navigate to="/" replace />}
            />
            <Route path="/activity" element={<ActivityPage />} />
            <Route path="/requests" element={<RequestsPage />} />
            <Route
              path="/scrobble"
              element={canTrack ? <ScrobblePage /> : <Navigate to="/" replace />}
            />
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
        </RouteErrorBoundary>
      </AppShell.Main>

      <CommandPaletteModal
        navItems={allItems}
        opened={paletteOpened}
        onOpen={openPalette}
        onClose={closePalette}
      />

      {/* Opens itself off the setup flag, latched so a language pick's cache clear can't close it. */}
      {isAdmin && <SetupWizard />}
      {/* Only once the instance is past first-run: the wizard owns the screen while it is up, and
          nobody being handed a brand-new Maki needs to be told what changed in it. */}
      {setup?.completed && <LanguageAnnouncementModal />}
      {setup?.completed && <AppearanceAnnouncementModal />}
      {/* Owns a toast, not a piece of the page, so it sits outside Main and renders nothing. */}
      {isAdmin && <MetadataDumpProgress quiet={!setup?.completed} />}
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
