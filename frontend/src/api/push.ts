import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from './client'
import { getPushSubscription, unsubscribePush } from '../lib/pwa/push'
import type { InboxEventType } from './inbox'

export interface PushPrefs {
  types: InboxEventType[]
}

export interface PushSubscriptionBody {
  endpoint: string
  keys: { p256dh: string; auth: string }
}

export function toSubscriptionBody(subscription: PushSubscription): PushSubscriptionBody | null {
  const { endpoint, keys } = subscription.toJSON()
  if (!endpoint || !keys?.p256dh || !keys.auth) return null
  return { endpoint, keys: { p256dh: keys.p256dh, auth: keys.auth } }
}

export const pushPublicKeyQuery = {
  queryKey: ['push', 'public-key'],
  queryFn: () => api<{ publicKey: string }>('/push/public-key'),
  staleTime: Infinity,
} as const

export function usePushPrefs(enabled = true) {
  return useQuery({
    queryKey: ['push', 'prefs'],
    queryFn: () => api<PushPrefs>('/push/prefs'),
    enabled,
  })
}

export function useSavePushPrefs() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (types: InboxEventType[]) =>
      api<PushPrefs>('/push/prefs', { method: 'PUT', body: JSON.stringify({ types }) }),
    onSuccess: (prefs) => queryClient.setQueryData(['push', 'prefs'], prefs),
  })
}

export function useSubscribePush() {
  return useMutation({
    mutationFn: (body: PushSubscriptionBody) =>
      api<{ subscribed: boolean }>('/push/subscription', { method: 'POST', body: JSON.stringify(body) }),
  })
}

export function useUnsubscribePush() {
  return useMutation({
    mutationFn: (endpoint: string) =>
      api<void>('/push/subscription', { method: 'DELETE', body: JSON.stringify({ endpoint }) }),
  })
}

export function useSendPushTest() {
  return useMutation({
    mutationFn: () => api<{ sent: number; failed: number; error: string | null }>('/push/test', { method: 'POST' }),
  })
}

const syncedUsers = new Set<number>()

/**
 * After a sign-in, re-posts this device's subscription so the server ties it to the account that
 * just signed in, whoever held the endpoint before. Once per user per page load; failures are
 * silent, the next sign-in tries again.
 */
export async function syncPushSubscription(userId: number): Promise<void> {
  if (syncedUsers.has(userId)) return
  syncedUsers.add(userId)
  try {
    await postThisDevice()
  } catch {
    syncedUsers.delete(userId)
  }
}

async function postThisDevice(): Promise<void> {
  const subscription = await getPushSubscription()
  const body = subscription && toSubscriptionBody(subscription)
  if (!body) return
  await api<unknown>('/push/subscription', { method: 'POST', body: JSON.stringify(body) })
}

/**
 * Signing other sessions out drops every subscription the account holds, this device's too. This
 * device stays signed in, so it registers again at once. Never throws.
 */
export async function repostPushSubscription(): Promise<void> {
  try {
    await postThisDevice()
  } catch {
    // The next page load's sync tries again.
  }
}

const DROP_TIMEOUT_MS = 4000

/**
 * Before an explicit logout: forgets this device's subscription locally and on the server, so the
 * next person to sign in here is not sent the previous account's notifications. Never throws and
 * never waits longer than a few seconds, so a dead push service cannot hold up signing out.
 */
export async function dropPushSubscription(): Promise<void> {
  syncedUsers.clear()
  const drop = async () => {
    const endpoint = await unsubscribePush()
    if (endpoint) {
      await api<void>('/push/subscription', { method: 'DELETE', body: JSON.stringify({ endpoint }) })
    }
  }
  try {
    await Promise.race([drop(), new Promise<void>((resolve) => setTimeout(resolve, DROP_TIMEOUT_MS))])
  } catch {
    // Best effort.
  }
}
