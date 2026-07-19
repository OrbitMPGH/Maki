import {
  IconActivity,
  IconFolderDown,
  IconHistory,
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
      { label: 'Library', path: '/', icon: IconLibrary, end: true },
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

export function isActive(item: NavItem, pathname: string): boolean {
  return item.end ? pathname === item.path : pathname.startsWith(item.path)
}

export function pageTitle(pathname: string): string {
  if (pathname.startsWith('/series/')) return 'Series'
  const match = ALL_ITEMS.find((i) => isActive(i, pathname))
  return match?.label ?? 'Maki'
}
