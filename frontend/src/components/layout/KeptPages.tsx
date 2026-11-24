import {
  Activity,
  memo,
  useLayoutEffect,
  useMemo,
  useState,
  type ComponentType,
  type ReactNode,
} from 'react'
import { MantineThemeProvider, type MantineThemeOverride, type PortalProps } from '@mantine/core'
import { matchPath, Route, Routes, useLocation, type Location } from 'react-router-dom'
// Home and Library stay eagerly imported: "/" resolves to one of the two on every cold load
// (StartPageRedirect), so splitting them would only add a round trip to the first paint.
import HomePage from '../../pages/HomePage'
import LibraryPage from '../../pages/LibraryPage'

type Key = 'home' | 'library' | 'discover'

interface Kept {
  paths: string[]
  routes: ReactNode
}

const KEYS: Key[] = ['home', 'library', 'discover']

// matchPath, so a trailing slash or another letter case lands where the router would send it.
const matches = (kept: Kept, path: string) => kept.paths.some((pattern) => matchPath(pattern, path))

/** Memoized on a stable location and routes, so a hidden page sits out the shell's re-renders. */
const KeptPage = memo(function KeptPage({ at, routes }: { at: Location; routes: ReactNode }) {
  return <Routes location={at}>{routes}</Routes>
})

/**
 * Gives one kept page its own portal host on `document.body`, hidden whenever its Activity is
 * hidden. React's Activity stops at the first host element when setting `display: none`, so
 * anything portalled out of a kept page (modals, menus, popovers, tooltips) would otherwise stay
 * live over the next route, including a closing modal whose transition timers were cancelled.
 */
function KeptSlot({
  name,
  visible,
  at,
  routes,
}: {
  name: Key
  visible: boolean
  at: Location
  routes: ReactNode
}) {
  const [portalNode] = useState(() => {
    const el = document.createElement('div')
    el.setAttribute('data-portal', 'true')
    el.setAttribute('data-kept-portal', name)
    return el
  })

  useLayoutEffect(() => {
    // Sit ahead of Mantine's shared portal node so shell overlays (CommandPalette, toasts) win
    // DOM-order ties against portals owned by a kept page.
    const shared = document.querySelector('[data-mantine-shared-portal-node]')
    if (shared && shared.parentNode === document.body) {
      document.body.insertBefore(portalNode, shared)
    } else {
      document.body.appendChild(portalNode)
    }
    return () => {
      portalNode.remove()
    }
  }, [portalNode])

  useLayoutEffect(() => {
    portalNode.style.display = visible ? '' : 'none'
  }, [portalNode, visible])

  const portalTheme = useMemo<MantineThemeOverride>(
    () => ({
      components: {
        Portal: { defaultProps: (): Partial<PortalProps> => ({ target: portalNode }) },
      },
    }),
    [portalNode],
  )

  return (
    <MantineThemeProvider theme={portalTheme}>
      <Activity mode={visible ? 'visible' : 'hidden'}>
        <KeptPage at={at} routes={routes} />
      </Activity>
    </MantineThemeProvider>
  )
}

/**
 * Keeps Home, the Library and Discover mounted but hidden once opened, so going back to one shows it
 * as it was left. Rebuilding one re-rendered every card and decoded every cover again, which on an iPad
 * held each switch for most of a second.
 *
 * Each hidden page renders against its own last location, so another page's URL never reaches its
 * filters. The shell's Routes answer these paths with nothing, since this draws them.
 *
 * Hiding a page drops the timers of any Mantine transition on it. Stock Mantine never restarts them,
 * so a modal closed just before leaving stayed mounted at opacity 0: `KeptSlot` hides the page's
 * portals while the page is away, and `patches/@mantine+core+*.patch` settles any interrupted
 * transition before paint when the page shows again.
 */
export function KeptPages({ homeEnabled, Discover }: { homeEnabled: boolean; Discover: ComponentType }) {
  const location = useLocation()
  const [kept, setKept] = useState<Partial<Record<Key, Location>>>({})
  const pages = useMemo<Record<Key, Kept>>(
    () => ({
      home: { paths: ['/home'], routes: <Route path="/home" element={<HomePage />} /> },
      library: {
        paths: ['/library', '/library/archived'],
        routes: (
          <>
            <Route path="/library" element={<LibraryPage />} />
            <Route path="/library/archived" element={<LibraryPage scope="archived" />} />
          </>
        ),
      },
      discover: { paths: ['/discover/:tab?'], routes: <Route path="/discover/:tab?" element={<Discover />} /> },
    }),
    [Discover],
  )
  const current = KEYS.find((key) => matches(pages[key], location.pathname) && (key !== 'home' || homeEnabled))
  if (current && kept[current] !== location) setKept({ ...kept, [current]: location })
  else if (!homeEnabled && kept.home) setKept({ ...kept, home: undefined })

  return KEYS.map((key) => {
    const at = key === current ? location : kept[key]
    if (!at) return null
    return (
      <KeptSlot
        key={key}
        name={key}
        visible={key === current}
        at={at}
        routes={pages[key].routes}
      />
    )
  })
}
