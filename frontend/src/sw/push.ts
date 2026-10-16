import { sw } from './scope'

/** Placeholder until push delivery lands. */
export function registerPushHandlers(): void {
  sw.addEventListener('push', () => {})
  sw.addEventListener('notificationclick', () => {})
}
