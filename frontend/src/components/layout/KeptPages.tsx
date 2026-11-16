import { Activity, useState, type ReactNode } from 'react'
import { Route, Routes, useLocation, type Location } from 'react-router-dom'
import HomePage from '../../pages/HomePage'
import LibraryPage from '../../pages/LibraryPage'

interface Kept {
  matches: (path: string) => boolean
  routes: ReactNode
}

type Key = 'home' | 'library' | 'discover'

const pages = (discover: ReactNode): Record<Key, Kept> => ({
  home: {
    matches: (path) => path === '/home',
    routes: <Route path="/home" element={<HomePage />} />,
  },
  library: {
    matches: (path) => path === '/library' || path === '/library/archived',
    routes: (
      <>
        <Route path="/library" element={<LibraryPage />} />
        <Route path="/library/archived" element={<LibraryPage scope="archived" />} />
      </>
    ),
  },
  discover: {
    matches: (path) => path === '/discover' || path.startsWith('/discover/'),
    routes: <Route path="/discover/:tab?" element={discover} />,
  },
})
const KEYS: Key[] = ['home', 'library', 'discover']

/**
 * Keeps Home, the Library and Discover mounted but hidden once opened, so going back to one shows it
 * as it was left. Rebuilding one re-rendered every card and decoded every cover again, which on an iPad
 * held each switch for most of a second.
 *
 * Each hidden page renders against its own last location, so another page's URL never reaches its
 * filters. The shell's Routes answer these paths with nothing, since this draws them.
 */
export function KeptPages({ homeEnabled, discover }: { homeEnabled: boolean; discover: ReactNode }) {
  const location = useLocation()
  const [kept, setKept] = useState<Partial<Record<Key, Location>>>({})
  const PAGES = pages(discover)
  const current = KEYS.find((key) => PAGES[key].matches(location.pathname) && (key !== 'home' || homeEnabled))
  if (current && kept[current] !== location) setKept({ ...kept, [current]: location })
  if (!homeEnabled && kept.home) setKept({ ...kept, home: undefined })

  return KEYS.map((key) => {
    const at = key === current ? location : kept[key]
    if (!at) return null
    return (
      <Activity key={key} mode={key === current ? 'visible' : 'hidden'}>
        <Routes location={at}>{PAGES[key].routes}</Routes>
      </Activity>
    )
  })
}
