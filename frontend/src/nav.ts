import {
  IconActivity,
  IconFolderDown,
  IconHistory,
  IconHome,
  IconInbox,
  IconLibrary,
  IconPlus,
  IconRefreshDot,
  IconSettings,
  IconSparkles,
  type Icon,
} from '@tabler/icons-react'

export interface NavItem {
  label: string
  path: string
  icon: Icon
  end?: boolean
}

export const NAV_SECTIONS: { label: string; items: NavItem[] }[] = [
  {
    label: 'Collection',
    items: [
      // Both carry real paths of their own; "/" is a redirect to whichever the user chose as
      // their start page (see StartPageRedirect in App.tsx), not a page. That's also why neither
      // needs `end` any more, since nothing here prefix-matches anything else.
      { label: 'Home', path: '/home', icon: IconHome },
      { label: 'Library', path: '/library', icon: IconLibrary },
      { label: 'Add series', path: '/add', icon: IconPlus },
      { label: 'Discover', path: '/discover', icon: IconSparkles },
      { label: 'Import', path: '/import', icon: IconFolderDown },
      // "Stats" rather than "Rewind": the page is a standing reading dashboard, and Rewind is the
      // year playback it launches. Naming the whole thing after the once-a-year part is what made
      // it read as somewhere you visit in January. /rewind still redirects here.
      { label: 'Stats', path: '/stats', icon: IconHistory },
    ],
  },
  {
    label: 'Automation',
    items: [
      { label: 'Activity', path: '/activity', icon: IconActivity },
      { label: 'Requests', path: '/requests', icon: IconInbox },
      { label: 'Scrobble', path: '/scrobble', icon: IconRefreshDot },
    ],
  },
  {
    label: 'System',
    items: [{ label: 'Settings', path: '/settings', icon: IconSettings }],
  },
]

export const ALL_ITEMS = NAV_SECTIONS.flatMap((s) => s.items)

export interface NavAvailability {
  discoverAvailable: boolean
  homeEnabled: boolean
  /** Holds AddSeries: decides whether /add reads "Add series" or "Request series". */
  canAdd: boolean
  /** Whether the Requests tab is worth showing: an admin actions them, a requester tracks theirs. */
  requestsVisible: boolean
}

/**
 * Hides tabs that can't work rather than showing ones that error or land nowhere:
 * Discover needs the local MangaBaka database, Home can be switched off entirely by anyone
 * who doesn't read in Maki (its route then redirects to the library), and Requests is only
 * meaningful to an admin or to someone who has to ask one.
 *
 * Cosmetic, like every permission check in the client: every endpoint behind these tabs
 * authorizes on its own.
 */
export function navSections({
  discoverAvailable,
  homeEnabled,
  canAdd,
  requestsVisible,
}: NavAvailability): typeof NAV_SECTIONS {
  const hidden = new Set<string>()
  if (!discoverAvailable) hidden.add('/discover')
  if (!homeEnabled) hidden.add('/home')
  if (!requestsVisible) hidden.add('/requests')

  return NAV_SECTIONS.map((section) => ({
    ...section,
    items: section.items
      .filter((item) => !hidden.has(item.path))
      .map((item) =>
        item.path === '/add' && !canAdd ? { ...item, label: 'Request series' } : item,
      ),
  }))
}

export function isActive(item: NavItem, pathname: string): boolean {
  return item.end ? pathname === item.path : pathname.startsWith(item.path)
}

export function pageTitle(pathname: string): string {
  if (pathname.startsWith('/series/')) return 'Series'
  const match = ALL_ITEMS.find((i) => isActive(i, pathname))
  return match?.label ?? 'Maki'
}
