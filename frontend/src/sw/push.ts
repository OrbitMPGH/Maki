import { sw } from './scope'

interface PushPayload {
  title?: string
  body?: string
  url?: string | null
  tag?: string | null
}

function parsePayload(event: PushEvent): PushPayload {
  try {
    return (event.data?.json() as PushPayload | undefined) ?? {}
  } catch {
    return { body: event.data?.text() }
  }
}

async function openUrl(path: string): Promise<void> {
  const target = new URL(path, sw.location.origin)
  const windows = await sw.clients.matchAll({ type: 'window', includeUncontrolled: true })
  const existing = windows.find((client) => new URL(client.url).origin === sw.location.origin)
  if (existing) {
    try {
      const focused = await existing.focus()
      await focused.navigate(target.href)
      return
    } catch {
      // navigate() is refused for a client this worker does not control; open a window instead.
    }
  }
  await sw.clients.openWindow(target.href)
}

export function registerPushHandlers(): void {
  sw.addEventListener('push', (event) => {
    const { title, body, url, tag } = parsePayload(event)
    event.waitUntil(
      sw.registration.showNotification(title || 'Maki', {
        body: body ?? '',
        icon: '/icons/icon-192.png',
        badge: '/icons/badge-96.png',
        ...(tag ? { tag } : {}),
        data: { url: url ?? '/' },
      }),
    )
  })

  sw.addEventListener('notificationclick', (event) => {
    event.notification.close()
    const url = (event.notification.data as { url?: string } | null)?.url ?? '/'
    event.waitUntil(openUrl(url))
  })
}
