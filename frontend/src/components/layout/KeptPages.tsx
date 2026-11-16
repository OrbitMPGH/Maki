import { Activity, memo, useMemo, useState, type ComponentType, type ReactNode } from 'react'
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
 * Keeps Home, the Library and Discover mounted but hidden once opened, so going back to one shows it
 * as it was left. Rebuilding one re-rendered every card and decoded every cover again, which on an iPad
 * held each switch for most of a second.
 *
 * Each hidden page renders against its own last location, so another page's URL never reaches its
 * filters. The shell's Routes answer these paths with nothing, since this draws them.
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
      <Activity key={key} mode={key === current ? 'visible' : 'hidden'}>
        <KeptPage at={at} routes={pages[key].routes} />
      </Activity>
    )
  })
}
