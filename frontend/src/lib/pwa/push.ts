import { pwaSupported } from './serviceWorker'

/** `PushManager` and `Notification`: missing over plain HTTP, and on iOS outside an installed app. */

export function pushSupported(): boolean {
  return pwaSupported() && 'PushManager' in window && 'Notification' in window
}

export function notificationPermission(): NotificationPermission | null {
  return pushSupported() ? Notification.permission : null
}

/** iOS and iPadOS deliver push only to an app added to the home screen, and hide `PushManager` otherwise. */
export function iosNeedsInstall(): boolean {
  try {
    const ua = navigator.userAgent
    const ios = /iPad|iPhone|iPod/.test(ua) || (ua.includes('Macintosh') && navigator.maxTouchPoints > 1)
    if (!ios) return false
    const standalone =
      window.matchMedia('(display-mode: standalone)').matches ||
      (navigator as Navigator & { standalone?: boolean }).standalone === true
    return !standalone
  } catch {
    return false
  }
}

export async function requestNotificationPermission(): Promise<NotificationPermission | null> {
  if (!pushSupported()) return null
  try {
    return await Notification.requestPermission()
  } catch {
    return null
  }
}

/** `getRegistration`, not `ready`: `ready` never settles when no worker is registered (dev builds). */
async function pushRegistration(): Promise<ServiceWorkerRegistration | null> {
  try {
    return (await navigator.serviceWorker.getRegistration()) ?? null
  } catch {
    return null
  }
}

export async function getPushSubscription(): Promise<PushSubscription | null> {
  if (!pushSupported()) return null
  try {
    const registration = await pushRegistration()
    return (await registration?.pushManager.getSubscription()) ?? null
  } catch {
    return null
  }
}

/** `applicationServerKey` is the server's VAPID public key, base64url as the server sends it. */
export async function subscribePush(applicationServerKey: string): Promise<PushSubscription | null> {
  if (!pushSupported()) return null
  const registration = await pushRegistration()
  if (!registration) return null
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
