import { useCallback, useEffect, useRef, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { t } from '@lingui/core/macro'
import { notifications } from '@mantine/notifications'
import { api, ApiError } from './client'
import type { RecommendationItem } from './hooks'
import {
  useMutateFeedback, useUndoFeedback, type FeedbackMutation, type FeedbackState,
} from './recommendationFeedback'
import { usePlanToReadAdd, usePlanToReadRemove, type PlanToReadEntry } from './planToRead'
import { randomUUID } from '../lib/uuid'

export type QueueOrigin = 'taste' | 'trending'
export type SwipeKind = 'want' | 'skip' | 'notForMe'

export interface QueueCard extends RecommendationItem {
  origin: QueueOrigin
  /** The feedback row's current revision for this title, 0 when none exists yet. Required by
   * `PUT feedback/{id}`'s `expectedRevision` and carried here so the queue never has to fetch it. */
  feedbackRevision: number
}

export interface QueueDeckResponse {
  cards: QueueCard[]
  generatedAt: string
  coldStart: boolean
  exhausted: boolean
}

export function fetchQueueDeck(params: {
  take?: number
  exclude: number[]
  refresh?: boolean
}): Promise<QueueDeckResponse> {
  return api<QueueDeckResponse>('/recommendations/queue', {
    method: 'POST',
    body: JSON.stringify({
      take: params.take ?? 12,
      exclude: params.exclude,
      refresh: params.refresh ?? false,
    }),
  })
}

const REFILL_THRESHOLD = 4
const UNDO_LIMIT = 10
const IDLE_INVALIDATE_MS = 2000

type QueueStatus = 'idle' | 'loading' | 'coldStart' | 'exhausted'

type SwipeTaskResult =
  | { kind: 'want'; entry: PlanToReadEntry }
  | { kind: 'skip' | 'notForMe'; result: FeedbackMutation }

interface UndoEntry {
  card: QueueCard
  kind: SwipeKind
  promise: Promise<SwipeTaskResult>
}

/**
 * The feedback row's revision after a 409, so a reinserted card can be swiped again instead of
 * 409ing forever on the stale deck-time value. A 404 means the row never existed, which reads as
 * revision 0, same as a card that has never been acted on.
 */
async function currentFeedbackRevision(id: number, fallback: number): Promise<number> {
  try {
    const state = await api<FeedbackState>(`/recommendations/feedback/${id}`)
    return state.revision
  } catch (err) {
    if (err instanceof ApiError && err.status === 404) return 0
    return fallback
  }
}

/**
 * The Discovery Queue's deck: a buffer of {@link QueueCard}s refilled from the server as it runs
 * low, with optimistic swipes and a 10-deep undo stack. See design 4.3.
 *
 * Only `['plan-to-read']` is invalidated per swipe (by the add/remove hooks themselves, which own
 * that key). `['recommendations']` and `['feedback-lab']` are debounced to unmount or 2s idle,
 * because they drive rails and panels well outside this tab and invalidating them on every card
 * would refetch those constantly while somebody is mid-session here.
 */
export function useQueueDeck() {
  const queryClient = useQueryClient()
  const mutateFeedback = useMutateFeedback()
  const undoFeedback = useUndoFeedback()
  const addPlan = usePlanToReadAdd()
  const removePlan = usePlanToReadRemove()

  const [cards, setCards] = useState<QueueCard[]>([])
  const [status, setStatus] = useState<QueueStatus>('idle')
  const [error, setError] = useState<unknown>(null)
  const [session, setSession] = useState({ swiped: 0, saved: 0 })
  const [canUndo, setCanUndo] = useState(false)

  const cardsRef = useRef<QueueCard[]>([])
  const swipedRef = useRef<Set<number>>(new Set())
  const undoStackRef = useRef<UndoEntry[]>([])
  const chainRef = useRef<Map<number, Promise<void>>>(new Map())
  const fetchingRef = useRef(false)
  const idleTimerRef = useRef<number | null>(null)
  const statusRef = useRef<QueueStatus>('idle')
  // The server's last `exhausted` flag. It can come back true on a deck that still holds cards
  // (deal those first), and once set it must stay set until the user acts (undo) or asks for a
  // manual retry, otherwise every swipe that drops the buffer below the refill threshold would
  // re-hit a source that just said it has nothing left.
  const exhaustedRef = useRef(false)
  // Set on a failed fetch, cleared on success or a forced retry. See the guard in `fetchMore`.
  const errorBlockedRef = useRef(false)

  const setCardsBoth = useCallback((next: QueueCard[]) => {
    cardsRef.current = next
    setCards(next)
  }, [])

  const setStatusBoth = useCallback((next: QueueStatus) => {
    statusRef.current = next
    setStatus(next)
  }, [])

  const invalidateRecommendations = useCallback(() => {
    for (const key of ['recommendations', 'feedback-lab']) {
      void queryClient.invalidateQueries({ queryKey: [key] })
    }
  }, [queryClient])

  const scheduleIdleInvalidate = useCallback(() => {
    if (idleTimerRef.current != null) window.clearTimeout(idleTimerRef.current)
    idleTimerRef.current = window.setTimeout(() => {
      idleTimerRef.current = null
      invalidateRecommendations()
    }, IDLE_INVALIDATE_MS)
  }, [invalidateRecommendations])

  useEffect(() => {
    return () => {
      if (idleTimerRef.current != null) window.clearTimeout(idleTimerRef.current)
      invalidateRecommendations()
    }
  }, [invalidateRecommendations])

  const fetchMore = useCallback(
    async (force = false) => {
      if (fetchingRef.current) return
      if (exhaustedRef.current && !force) {
        // Nothing to fetch until the user undoes a swipe or retries by hand; still reflect the
        // state if the buffer has meanwhile run dry.
        if (cardsRef.current.length === 0) setStatusBoth('exhausted')
        return
      }
      // A failed request also blocks auto-refill until a manual retry: without this, an empty
      // buffer plus a persistent error (the local dump missing, say) sets status back to 'idle',
      // which the refill effect below reads as "go ahead", firing the same failing request forever.
      if (errorBlockedRef.current && !force) return
      fetchingRef.current = true
      if (cardsRef.current.length === 0) setStatusBoth('loading')
      try {
        const exclude = [
          ...cardsRef.current.map((c) => Number(c.providerId)),
          ...swipedRef.current,
        ]
        const res = await fetchQueueDeck({ exclude })
        const seen = new Set(cardsRef.current.map((c) => c.providerId))
        const fresh = res.cards.filter((c) => !seen.has(c.providerId))
        const next = [...cardsRef.current, ...fresh]
        setCardsBoth(next)
        setError(null)
        errorBlockedRef.current = false
        exhaustedRef.current = res.exhausted
        if (next.length === 0) {
          setStatusBoth(res.coldStart ? 'coldStart' : 'exhausted')
        } else {
          setStatusBoth('idle')
        }
      } catch (err) {
        setError(err)
        errorBlockedRef.current = true
        if (cardsRef.current.length === 0) setStatusBoth('idle')
      } finally {
        fetchingRef.current = false
      }
    },
    [setCardsBoth, setStatusBoth],
  )

  useEffect(() => {
    if (cards.length < REFILL_THRESHOLD && status !== 'exhausted') {
      void fetchMore()
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [cards.length, status])

  const runChained = useCallback(
    <T,>(providerId: number, task: () => Promise<T>): Promise<T> => {
      const prior = chainRef.current.get(providerId) ?? Promise.resolve()
      const result = prior.then(task, task)
      chainRef.current.set(
        providerId,
        result.then(
          () => undefined,
          () => undefined,
        ),
      )
      return result
    },
    [],
  )

  const swipe = useCallback(
    (card: QueueCard, kind: SwipeKind) => {
      const providerId = Number(card.providerId)
      if (swipedRef.current.has(providerId)) return
      setCardsBoth(cardsRef.current.filter((c) => c.providerId !== card.providerId))
      swipedRef.current.add(providerId)
      setSession((s) => ({ swiped: s.swiped + 1, saved: kind === 'want' ? s.saved + 1 : s.saved }))

      const task = (): Promise<SwipeTaskResult> => {
        const clientMutationId = randomUUID()
        if (kind === 'want') {
          return addPlan
            .mutateAsync({ providerId, origin: card.origin, clientMutationId })
            .then((entry) => ({ kind: 'want' as const, entry }))
        }
        return mutateFeedback
          .mutateAsync({
            id: providerId,
            action: kind === 'skip' ? 'dismiss' : 'dislike',
            expectedRevision: card.feedbackRevision,
            clientMutationId,
          })
          .then((result) => ({ kind, result }))
      }

      let promise!: Promise<SwipeTaskResult>
      promise = runChained(providerId, task).catch(async (err) => {
        swipedRef.current.delete(providerId)
        // A 409 means the row moved under us (another action, or a previous undo) since the deck
        // handed out this revision. Refetch it so the reinserted card can be acted on again instead
        // of 409ing on every subsequent attempt.
        const revision =
          kind !== 'want' && err instanceof ApiError && err.status === 409
            ? await currentFeedbackRevision(providerId, card.feedbackRevision)
            : card.feedbackRevision
        setCardsBoth([{ ...card, feedbackRevision: revision }, ...cardsRef.current])
        setSession((s) => ({
          swiped: Math.max(0, s.swiped - 1),
          saved: kind === 'want' ? Math.max(0, s.saved - 1) : s.saved,
        }))
        notifications.show({ color: 'var(--danger)', message: t`Couldn't save that. Try again.` })
        // This attempt never produced anything undo-able; leaving it on the stack would make `z`
        // await a rejected promise and show a spurious "Couldn't undo" for nothing.
        undoStackRef.current = undoStackRef.current.filter((e) => e.promise !== promise)
        setCanUndo(undoStackRef.current.length > 0)
        throw err
      })
      promise.then(() => scheduleIdleInvalidate()).catch(() => {})

      undoStackRef.current = [...undoStackRef.current, { card, kind, promise }].slice(-UNDO_LIMIT)
      setCanUndo(true)
    },
    [addPlan, mutateFeedback, runChained, scheduleIdleInvalidate, setCardsBoth],
  )

  const undo = useCallback(async () => {
    const stack = undoStackRef.current
    if (stack.length === 0) return
    const entry = stack[stack.length - 1]
    undoStackRef.current = stack.slice(0, -1)
    setCanUndo(undoStackRef.current.length > 0)

    const { card, kind } = entry
    const providerId = Number(card.providerId)
    try {
      const outcome = await entry.promise
      // For a `want` undo the feedback row is untouched, so the card's revision stays whatever it
      // already was. For skip/dislike, the row's revision moves again on undo, and the server
      // checks the *row's* revision, not the profile-wide one `FeedbackMutation.feedbackRevision`
      // carries from `BumpAsync`; using that here 409s the undo itself.
      let reinsertCard = card
      if (outcome.kind === 'want') {
        await removePlan.mutateAsync(providerId)
      } else if (outcome.result.eventId != null) {
        const undoResult = await undoFeedback.mutateAsync({
          eventId: outcome.result.eventId,
          expectedRevision: outcome.result.state.revision,
          clientMutationId: randomUUID(),
        })
        reinsertCard = { ...card, feedbackRevision: undoResult.state.revision }
      }
      swipedRef.current.delete(providerId)
      // The user acted: a swipe that had exhausted the deck is undone, so the next refill is
      // allowed to ask the server again instead of sitting behind the exhausted guard.
      exhaustedRef.current = false
      if (statusRef.current === 'exhausted') setStatusBoth('idle')
      setCardsBoth([reinsertCard, ...cardsRef.current.filter((c) => c.providerId !== card.providerId)])
      setSession((s) => ({
        swiped: Math.max(0, s.swiped - 1),
        saved: kind === 'want' ? Math.max(0, s.saved - 1) : s.saved,
      }))
      scheduleIdleInvalidate()
    } catch {
      notifications.show({ color: 'var(--danger)', message: t`Couldn't undo. Try again.` })
    }
  }, [removePlan, undoFeedback, scheduleIdleInvalidate, setCardsBoth, setStatusBoth])

  return {
    cards,
    topCard: cards[0] ?? null,
    status,
    error,
    session,
    canUndo,
    swipe,
    undo,
    refresh: fetchMore,
  }
}
