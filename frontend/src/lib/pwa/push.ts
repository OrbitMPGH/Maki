import { pwaSupported } from './serviceWorker'

/** `PushManager` and `Notification`: missing over plain HTTP, and on iOS outside an installed app. */

export function pushSupported(): boolean {
  return pwaSupported() && 'PushManager' in window && 'Notification' in window
}

export function notificationPermission(): NotificationPermission | null {
  return pushSupported() ? Notification.permission : null
}

export async function requestNotificationPermission(): Promise<NotificationPermission | null> {
  if (!pushSupported()) return null
  try {
    return await Notification.requestPermission()
  } catch {
    return null
  }
}

export async function getPushSubscription(): Promise<PushSubscription | null> {
  if (!pushSupported()) return null
  try {
    const registration = await navigator.serviceWorker.ready
    return await registration.pushManager.getSubscription()
  } catch {
    return null
  }
}

/** `applicationServerKey` is the server's VAPID public key, base64url as the server sends it. */
export async function subscribePush(applicationServerKey: string): Promise<PushSubscription | null> {
  if (!pushSupported()) return null
  const registration = await navigator.serviceWorker.ready
  return registration.pushManager.subscribe({
    userVisibleOnly: true,
    applicationServerKey: base64UrlToBytes(applicationServerKey),
  })
}

/** Drops this device's subscription. Returns the endpoint that was dropped, for the server call. */
export async function unsubscribePush(): Promise<string | null> {
  const subscription = await getPushSubscription()
  if (!subscription) return null
  const { endpoint } = subscription
  try {
    await subscription.unsubscribe()
  } catch {
    // Already gone at the push service; the server side is what still needs telling.
  }
  return endpoint
}

function base64UrlToBytes(value: string): Uint8Array<ArrayBuffer> {
  const padded = value.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(value.length / 4) * 4, '=')
  const raw = atob(padded)
  const bytes = new Uint8Array(new ArrayBuffer(raw.length))
  for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i)
  return bytes
}
