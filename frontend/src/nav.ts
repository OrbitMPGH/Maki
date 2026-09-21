import {
  IconActivity,
  IconFolderDown,
  IconHistory,
  IconHeartbeat,
  IconHome,
  IconInbox,
  IconLibrary,
  IconPlus,
  IconRefreshDot,
  IconSettings,
  IconSparkles,
  type Icon,
} from '@tabler/icons-react'
import { msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'

export interface NavItem {
  /**
   * Held as a descriptor rather than a rendered string because this table is built once at module
   * scope, and a string baked in there would be whatever language was active when the module first
   * evaluated. Callers render it, which also means the type will not let one forget.
   */
  label: MessageDescriptor
  path: string
  icon: Icon
  end?: boolean
}

export const NAV_SECTIONS: { label: MessageDescriptor; items: NavItem[] }[] = [
  {
    label: msg`Collection`,
    items: [
      // Both carry real paths of their own; "/" is a redirect to whichever the user chose as
      // their start page (see StartPageRedirect in App.tsx), not a page. That's also why neither
      // needs `end` any more, since nothing here prefix-matches anything else.
      { label: msg`Home`, path: '/home', icon: IconHome },
      { label: msg`Library`, path: '/library', icon: IconLibrary },
      { label: msg`Add series`, path: '/add', icon: IconPlus },
      { label: msg`Discover`, path: '/discover', icon: IconSparkles },
      { label: msg`Import`, path: '/import', icon: IconFolderDown },
      // "Stats" rather than "Rewind": the page is a standing reading dashboard, and Rewind is the
      // year playback it launches. Naming the whole thing after the once-a-year part is what made
      // it read as somewhere you visit in January. /rewind still redirects here.
      { label: msg`Stats`, path: '/stats', icon: IconHistory },
    ],
  },
  {
    label: msg`Automation`,
    items: [
      { label: msg`Activity`, path: '/activity', icon: IconActivity },
      { label: msg`Requests`, path: '/requests', icon: IconInbox },
      { label: msg`Scrobble`, path: '/scrobble', icon: IconRefreshDot },
    ],
  },
  {
    label: msg`System`,
    items: [{ label: msg`Health`, path: '/health', icon: IconHeartbeat }, { label: msg`Settings`, path: '/settings', icon: IconSettings }],
  },
]

export const ALL_ITEMS = NAV_SECTIONS.flatMap((s) => s.items)

export interface NavAvailability {
  isAdmin?: boolean
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
  isAdmin = false,
}: NavAvailability): typeof NAV_SECTIONS {
  const hidden = new Set<string>()
  if (!isAdmin) hidden.add('/health')
  if (!discoverAvailable) hidden.add('/discover')
  if (!homeEnabled) hidden.add('/home')
  if (!requestsVisible) hidden.add('/requests')

  return NAV_SECTIONS.map((section) => ({
    ...section,
    items: section.items
      .filter((item) => !hidden.has(item.path))
      .map((item) =>
        item.path === '/add' && !canAdd ? { ...item, label: msg`Request series` } : item,
      ),
  }))
}

export function isActive(item: NavItem, pathname: string): boolean {
  return item.end ? pathname === item.path : pathname.startsWith(item.path)
}

/**
 * What to call the page at this path, or null when nothing here names it.
 *
 * Null rather than a generic fallback because the two callers want different generic words: the
 * header wants the product name, a back link wants "Back". Answering one of them here meant the
 * other compared against that string to undo it.
 */
export function pageTitle(pathname: string): MessageDescriptor | null {
  if (pathname.startsWith('/series/')) return msg`Series`
  // Reached from the header bell rather than the nav, so it has no NavItem to take a label from.
  if (pathname.startsWith('/notifications')) return msg`Notifications`
  return ALL_ITEMS.find((i) => isActive(i, pathname))?.label ?? null
}
