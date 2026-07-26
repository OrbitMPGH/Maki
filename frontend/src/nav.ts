import {
  IconActivity,
  IconFolderDown,
  IconHistory,
  IconHome,
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
      // needs `end` any more — nothing here prefix-matches anything else.
      { label: 'Home', path: '/home', icon: IconHome },
      { label: 'Library', path: '/library', icon: IconLibrary },
      { label: 'Add series', path: '/add', icon: IconPlus },
      { label: 'Discover', path: '/discover', icon: IconSparkles },
      { label: 'Import', path: '/import', icon: IconFolderDown },
      { label: 'Rewind', path: '/rewind', icon: IconHistory },
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

export const ALL_ITEMS = NAV_SECTIONS.flatMap((s) => s.items)

/**
 * Hides tabs that can't work rather than showing ones that error or land nowhere:
 * Discover needs the local MangaBaka database, and Home can be switched off entirely by anyone
 * who doesn't read in Maki (its route then redirects to the library).
 */
export function navSections(discoverAvailable: boolean, homeEnabled = true): typeof NAV_SECTIONS {
  if (discoverAvailable && homeEnabled) return NAV_SECTIONS

  const hidden = new Set<string>()
  if (!discoverAvailable) hidden.add('/discover')
  if (!homeEnabled) hidden.add('/home')

  return NAV_SECTIONS.map((section) => ({
    ...section,
    items: section.items.filter((item) => !hidden.has(item.path)),
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
