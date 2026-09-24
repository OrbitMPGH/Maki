import { useCallback, useEffect, useRef, useState } from 'react'
import { Badge, Center, Loader, Text, UnstyledButton, VisuallyHidden } from '@mantine/core'
import { IconCards } from '@tabler/icons-react'
import { Link } from 'react-router-dom'
import { Trans, useLingui } from '@lingui/react/macro'
import { useQueueDeck, type QueueCard, type SwipeKind } from '../../api/discoverQueue'
import { usePlanToRead } from '../../api/planToRead'
import { useRootFolders } from '../../api/hooks'
import { DiscoverDetailModal } from '../../components/discover/DiscoverDetailModal'
import { CardStack, type CardStackHandle } from '../../components/discover/queue/CardStack'
import { QueueActionBar } from '../../components/discover/queue/QueueActionBar'
import { QueueEmptyState } from '../../components/discover/queue/QueueEmptyState'

const HINT_SEEN_KEY = 'discover-queue-hint-seen'

function hintAlreadySeen(): boolean {
  try {
    return localStorage.getItem(HINT_SEEN_KEY) === 'true'
  } catch {
    return true
  }
}

function markHintSeen(): void {
  try {
    localStorage.setItem(HINT_SEEN_KEY, 'true')
  } catch {
    // Private browsing or a blocked store: the hint just shows again next visit, which is fine.
  }
}

/**
 * The Discovery Queue: one candidate at a time, swiped from the existing recommendation engine.
 * See the design doc section 4 and `.claude/rules/recommendations.md`.
 */
export function QueueTab() {
  const { t } = useLingui()
  const { cards, topCard, status, error, session, canUndo, swipe, undo, refresh } = useQueueDeck()
  const { data: planEntries } = usePlanToRead()
  const { data: rootFolders } = useRootFolders()

  const [detailCard, setDetailCard] = useState<QueueCard | null>(null)
  const [announcement, setAnnouncement] = useState('')
  const [showHint, setShowHint] = useState(false)
  const stackRef = useRef<CardStackHandle | null>(null)

  useEffect(() => {
    if (!hintAlreadySeen() && cards.length > 0) setShowHint(true)
  }, [cards.length])

  const dismissHint = useCallback(() => {
    if (showHint) {
      setShowHint(false)
      markHintSeen()
    }
  }, [showHint])

  const announce = useCallback(
    (kind: SwipeKind, title: string) => {
      const text =
        kind === 'want'
          ? t`Saved ${title}`
          : kind === 'skip'
            ? t`Skipped ${title}`
            : t`Hidden ${title}`
      setAnnouncement(text)
    },
    [t],
  )

  const handleSwipe = useCallback(
    (card: QueueCard, kind: SwipeKind) => {
      dismissHint()
      announce(kind, card.title)
      swipe(card, kind)
    },
    [announce, dismissHint, swipe],
  )

  const buttonSwipe = useCallback(
    (kind: SwipeKind) => {
      if (!topCard) return
      void stackRef.current?.commitTop(kind)
    },
    [topCard],
  )

  const blocked = detailCard != null

  // Keyboard control, bound only while this tab is mounted and no modal covers it.
  useEffect(() => {
    if (blocked) return
    const handler = (e: KeyboardEvent) => {
      if (!topCard) return
      const target = e.target as HTMLElement | null
      if (target?.closest('input, textarea, select, [contenteditable], [role="dialog"]')) return
      if (e.key === ' ' && target?.closest('button, a')) return
      switch (e.key) {
        case 'ArrowRight':
          e.preventDefault()
          buttonSwipe('want')
          break
        case 'ArrowLeft':
          e.preventDefault()
          buttonSwipe('skip')
          break
        case 'ArrowDown':
          e.preventDefault()
          buttonSwipe('notForMe')
          break
        case ' ':
          e.preventDefault()
          setDetailCard(topCard)
          break
        case 'z':
        case 'Z':
          if (canUndo) void undo()
          break
      }
    }
    window.addEventListener('keydown', handler)
    return () => window.removeEventListener('keydown', handler)
  }, [blocked, topCard, buttonSwipe, canUndo, undo])

  const wantCount = planEntries?.length ?? 0
  const swipedCount = session.swiped

  return (
    <div className="queue-tab">
      <div className="queue-header">
        <Text size="sm" c="var(--ink-3)">
          <Trans>{swipedCount} reviewed this session</Trans>
        </Text>
        <UnstyledButton className="queue-want-pill" component={Link} to="/shortlist">
          <Badge
            size="lg"
            variant="light"
            color="var(--info)"
            leftSection={<IconCards size={14} />}
          >
            <Trans>Shortlist · {wantCount}</Trans>
          </Badge>
        </UnstyledButton>
      </div>

      <div className="queue-body">
        {status === 'loading' && cards.length === 0 && (
          <Center py={80}>
            <Loader size="sm" />
          </Center>
        )}

        {Boolean(error) && cards.length === 0 && (
          <QueueEmptyState kind="error" error={error} onRetry={() => void refresh(true)} />
        )}

        {!error && status === 'coldStart' && cards.length === 0 && <QueueEmptyState kind="coldStart" />}

        {!error && status === 'exhausted' && cards.length === 0 && (
          <QueueEmptyState kind="exhausted" onRetry={() => void refresh(true)} />
        )}

        {cards.length > 0 && (
          <>
            <CardStack ref={stackRef} cards={cards} onSwipe={handleSwipe} onOpen={setDetailCard} />
            {showHint && (
              <Text className="queue-hint" size="xs" c="var(--ink-3)" ta="center">
                <Trans>Swipe, or use the buttons below</Trans>
              </Text>
            )}
          </>
        )}
      </div>

      <QueueActionBar
        disabled={!topCard}
        canUndo={canUndo}
        onWant={() => buttonSwipe('want')}
        onSkip={() => buttonSwipe('skip')}
        onNotForMe={() => buttonSwipe('notForMe')}
        onInfo={() => topCard && setDetailCard(topCard)}
        onUndo={() => void undo()}
      />

      <VisuallyHidden aria-live="polite">{announcement}</VisuallyHidden>

      <DiscoverDetailModal
        item={detailCard}
        feedbackContext={{ surface: 'queue' }}
        inLibrarySeriesId={null}
        rootFolders={rootFolders}
        onClose={() => setDetailCard(null)}
      />
    </div>
  )
}
