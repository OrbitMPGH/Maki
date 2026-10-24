import { http, HttpResponse } from 'msw'
import type { HttpHandler } from 'msw'
import type { InboxEventType, InboxItem, InboxPage, InboxPrefs } from '../../../src/api/inbox'
import { api, cover, daysAgo, hoursAgo, minutesAgo } from '../api'
import { seriesById } from '../shared'

interface ItemInit {
  id: number
  type: InboxEventType
  title: string
  body: string
  at: string
  level?: InboxItem['level']
  seriesId?: number
  chapterId?: number
  url?: string | null
  read?: boolean
}

function item(init: ItemInit): InboxItem {
  return {
    id: init.id,
    type: init.type,
    level: init.level ?? 'info',
    title: init.title,
    body: init.body,
    seriesId: init.seriesId ?? null,
    chapterId: init.chapterId ?? null,
    url: init.url !== undefined ? init.url : init.seriesId ? `/series/${init.seriesId}` : null,
    coverUrl: init.seriesId ? cover(init.seriesId) : null,
    createdAt: init.at,
    read: init.read ?? false,
  } satisfies InboxItem
}

const title = (id: number) => seriesById(id).title

/** One of most event kinds, newest first, spread over today, yesterday and older days. */
export const inboxItems: InboxItem[] = [
  item({ id: 120, type: 'newChapterAvailable', title: `New chapters: ${title(1)}`, body: 'Chapters 142 and 143 are available.', at: minutesAgo(5), seriesId: 1, chapterId: 4201 }),
  item({ id: 119, type: 'chapterDownloaded', title: `Downloaded: ${title(5)}`, body: 'Chapter 34 is ready to read.', at: minutesAgo(42), seriesId: 5, chapterId: 4120 }),
  item({ id: 118, type: 'downloadFailed', level: 'error', title: `Download failed: ${title(3)}`, body: 'Chapter 89 failed after 5 attempts. The source answered 403.', at: hoursAgo(2), seriesId: 3, chapterId: 4090, url: '/activity' }),
  item({ id: 117, type: 'achievementUnlocked', title: 'Achievement unlocked: Night Owl', body: 'You read for an hour after midnight.', at: hoursAgo(3), url: '/stats' }),
  item({ id: 116, type: 'levelUp', title: 'You reached level 12', body: '350 XP to level 13.', at: hoursAgo(3), url: '/stats', read: true }),
  item({ id: 115, type: 'requestSubmitted', title: 'New request from Rin', body: `Rin asked for ${title(11)}.`, at: hoursAgo(5), seriesId: 11, url: '/requests', read: true }),
  item({ id: 114, type: 'healthIssue', level: 'warning', title: 'Library folder is almost full', body: '/library has 2.1 GB free. Downloads will pause below 1 GB.', at: hoursAgo(7), url: '/health' }),
  item({ id: 113, type: 'updateAvailable', title: 'Maki 0.33.0 is available', body: 'You are on 0.32.0.', at: hoursAgo(9), url: '/settings?tab=system', read: true }),
  item({ id: 112, type: 'smartDownloadQueued', title: `Smart Download: ${title(6)}`, body: '5 chapters were queued because you are close to the end.', at: hoursAgo(26), seriesId: 6, read: true }),
  item({ id: 111, type: 'followedCreatorRelease', title: 'New series from Aoi Mizuhara', body: 'A creator you follow has a new series on MangaBaka.', at: hoursAgo(28), url: '/discover' }),
  item({ id: 110, type: 'chapterUpgraded', title: `Chapter upgraded: ${title(9)}`, body: 'Chapter 18 was replaced by a higher quality release.', at: hoursAgo(31), seriesId: 9, read: true }),
  item({ id: 109, type: 'seriesStatusChanged', title: `${title(4)} is on hiatus`, body: 'The status changed from Ongoing to Hiatus.', at: daysAgo(2), seriesId: 4, read: true }),
  item({ id: 108, type: 'backupFinished', title: 'Backup taken', body: 'The database was backed up (84 MB).', at: daysAgo(3), url: '/settings?tab=system', read: true }),
  item({ id: 107, type: 'importFinished', title: 'Library import finished', body: '18 series imported, 2 need a manual match.', at: daysAgo(3), url: '/import', read: true }),
  item({ id: 106, type: 'sourceMatchFinished', title: `Sources linked: ${title(2)}`, body: 'Matched 2 sources.', at: daysAgo(4), seriesId: 2, read: true }),
  item({ id: 105, type: 'requestApproved', title: 'Your request was approved', body: `${title(8)} was added to the library.`, at: daysAgo(4), seriesId: 8, read: true }),
  item({ id: 104, type: 'volumeUpgraded', title: `Volume upgraded: ${title(10)}`, body: 'Volume 3 replaced 9 chapter files.', at: daysAgo(5), seriesId: 10, read: true }),
  item({ id: 103, type: 'upgradeRestoreFailed', level: 'error', title: `Upgrade restore failed: ${title(7)}`, body: 'Chapter 41 could not be put back and is in the trash folder.', at: daysAgo(6), seriesId: 7, read: true }),
  item({ id: 102, type: 'accountSecurity', level: 'warning', title: 'New sign-in', body: 'Your account signed in from a browser Maki has not seen before.', at: daysAgo(8), url: '/settings?tab=account&s=sign-in', read: true }),
  item({ id: 101, type: 'torrentProposalPending', level: 'warning', title: `Volume release proposed: ${title(12)}`, body: 'A torrent release could replace files in this series.', at: daysAgo(9), seriesId: 12, read: true }),
  item({ id: 100, type: 'importListFinished', title: 'Import list finished', body: 'AniList sync added 3 series and requested 1.', at: daysAgo(10), read: true }),
]

const ADMIN_ONLY: InboxEventType[] = ['requestSubmitted', 'healthIssue', 'updateAvailable', 'importFinished', 'backupFinished', 'upgradeRestoreFailed']

/** What a non-admin's feed holds: the same events minus the instance-level ones. */
export const readerInboxItems: InboxItem[] = inboxItems.filter((i) => !ADMIN_ONLY.includes(i.type))

/** A long backlog for the "Load more" and "99+" cases. */
export const longInbox: InboxItem[] = Array.from({ length: 120 }, (_, n) => {
  const base = inboxItems[n % 8]
  return { ...base, id: 500 - n, createdAt: hoursAgo(n * 2 + 1), read: n > 6 }
})

export const inboxPrefs: InboxPrefs = {
  types: Object.fromEntries(
    [
      'newChapterAvailable',
      'smartDownloadQueued',
      'chapterDownloaded',
      'downloadFailed',
      'achievementUnlocked',
      'levelUp',
      'requestSubmitted',
      'requestApproved',
      'requestRejected',
      'requestEdited',
      'healthIssue',
      'updateAvailable',
      'importFinished',
      'backupFinished',
    ].map((k) => [k, true]),
  ),
  toasts: true,
  seriesDefault: 'All',
}

/**
 * A working inbox over `items`: paging by `before`, `unreadOnly`, `type`, and the read, dismiss and
 * clear mutations actually change what the next GET returns. Call it once per story run (from
 * `beforeEach` with `msw.use`) so a play function's changes never leak into the next run.
 */
export function inboxHandlers(items: InboxItem[] = inboxItems, options: { pageSize?: number } = {}): HttpHandler[] {
  const pageSize = options.pageSize ?? 12
  let live = items.map((i) => ({ ...i }))
  const unread = () => live.filter((i) => !i.read).length

  return [
    http.get(api('/inbox'), ({ request }) => {
      const params = new URL(request.url).searchParams
      const before = params.get('before')
      const type = params.get('type')
      let list = [...live].sort((a, b) => b.id - a.id)
      if (before) list = list.filter((i) => i.id < Number(before))
      if (params.get('unreadOnly') === 'true') list = list.filter((i) => !i.read)
      if (type) list = list.filter((i) => i.type === type)
      const page = list.slice(0, pageSize)
      const body: InboxPage = {
        items: page,
        unread: unread(),
        nextCursor: list.length > pageSize ? page[page.length - 1].id : null,
      }
      return HttpResponse.json(body)
    }),
    http.get(api('/inbox/unread-count'), () => HttpResponse.json({ count: unread() })),
    http.get(api('/inbox/prefs'), () => HttpResponse.json(inboxPrefs)),
    http.post(api('/inbox/read-all'), () => {
      const marked = unread()
      live = live.map((i) => ({ ...i, read: true }))
      return HttpResponse.json({ marked })
    }),
    http.post(api('/inbox/:id/read'), ({ params }) => {
      live = live.map((i) => (i.id === Number(params.id) ? { ...i, read: true } : i))
      return new HttpResponse(null, { status: 204 })
    }),
    http.delete(api('/inbox/:id'), ({ params }) => {
      live = live.filter((i) => i.id !== Number(params.id))
      return new HttpResponse(null, { status: 204 })
    }),
    http.delete(api('/inbox'), () => {
      const deleted = live.length
      live = []
      return HttpResponse.json({ deleted })
    }),
  ]
}
