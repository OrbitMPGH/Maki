import type { ReactNode } from 'react'
import { Button, Group, Stack, Text, Title } from '@mantine/core'
import { IconPlus, IconQuestionMark } from '@tabler/icons-react'
import { Link } from 'react-router-dom'
import { IconBrandMark } from '../IconBrandMark'

type Art = 'shelf' | 'missing'
type Mood = 'asleep' | 'asking' | 'pleased'

/**
 * What a section says when it has nothing to show: a plain statement, one line on what would fill
 * it, and at most one thing to do about it. Left-aligned with the content it stands in for.
 * `compact` is for a section of an operational page, where an empty queue is the normal state and
 * should not take the screen.
 *
 * `art` is for when the whole page is empty rather than one section of it (an empty library, a
 * series or route that doesn't exist): centred in the page, over a row of empty cover slots
 * (`shelf`) or a lone blank cover (`missing`), with room for a second action.
 *
 * `mood` puts the mascot beside the text of the inline form (asleep for an empty queue or list,
 * pleased when there is nothing left to deal with, asking when it needs something from you).
 */
export function EmptyState({
  title,
  description,
  actionLabel,
  actionTo,
  onAction,
  compact,
  art,
  mood,
  code,
  headingOrder = 2,
  actionIcon,
  secondaryActionLabel,
  secondaryActionTo,
  secondaryActionIcon,
}: {
  title: string
  description?: ReactNode
  actionLabel?: string
  actionTo?: string
  onAction?: () => void
  compact?: boolean
  art?: Art
  mood?: Mood
  /** Printed on the `missing` cover: the id or path that led nowhere. */
  code?: string
  /** 1 when the page has no header of its own above the state. */
  headingOrder?: 1 | 2
  actionIcon?: ReactNode
  secondaryActionLabel?: string
  secondaryActionTo?: string
  secondaryActionIcon?: ReactNode
}) {
  if (art) {
    return (
      <div className="empty-state empty-state--page">
        {art === 'shelf' ? <ShelfArt addTo={actionTo} /> : <MissingArt code={code} />}
        <Title order={headingOrder} className="empty-state-title">
          {title}
        </Title>
        {description && <Text className="empty-state-description">{description}</Text>}
        {(actionLabel || secondaryActionLabel) && (
          <div className="empty-state-actions">
            {actionLabel &&
              (actionTo ? (
                <Button component={Link} to={actionTo} size="md" leftSection={actionIcon}>
                  {actionLabel}
                </Button>
              ) : (
                <Button onClick={onAction} size="md" leftSection={actionIcon}>
                  {actionLabel}
                </Button>
              ))}
            {secondaryActionLabel && secondaryActionTo && (
              <Button
                component={Link}
                to={secondaryActionTo}
                size="md"
                variant="default"
                leftSection={secondaryActionIcon}
              >
                {secondaryActionLabel}
              </Button>
            )}
          </div>
        )}
      </div>
    )
  }

  const lines = (
    <>
      <Text className="empty-state-title">{title}</Text>
      {description && (
        <Text c="var(--ink-3)" size="sm" maw={520}>
          {description}
        </Text>
      )}
      {actionLabel &&
        (actionTo ? (
          <Button component={Link} to={actionTo} mt="sm" variant="default" size="sm">
            {actionLabel}
          </Button>
        ) : (
          <Button onClick={onAction} mt="sm" variant="default" size="sm">
            {actionLabel}
          </Button>
        ))}
    </>
  )
  const py = compact ? 'sm' : 'xl'

  if (mood) {
    return (
      <Group className="empty-state" data-compact={compact || undefined} gap={16} wrap="nowrap" align="center" py={py}>
        <span className="empty-state-mark">
          <IconBrandMark mood={mood} size={56} />
        </span>
        <Stack align="flex-start" gap={4}>
          {lines}
        </Stack>
      </Group>
    )
  }

  return (
    <Stack className="empty-state" data-compact={compact || undefined} align="flex-start" gap={4} py={py}>
      {lines}
    </Stack>
  )
}

/**
 * Five cover slots fading out from a highlighted middle one. The middle slot also links to the
 * primary action for pointer users; it stays out of the tab order and the accessibility tree
 * because the button below is the same action.
 */
function ShelfArt({ addTo }: { addTo?: string }) {
  const lines = (
    <>
      <span className="empty-shelf-line" />
      <span className="empty-shelf-line" />
    </>
  )
  const middle = (
    <>
      <span className="empty-shelf-cover">
        <span className="empty-shelf-plus">
          <IconPlus size={24} stroke={2} />
        </span>
      </span>
      {lines}
    </>
  )
  return (
    <div className="empty-shelf" aria-hidden>
      {[2, 1, 0, 1, 2].map((depth, i) =>
        depth === 0 && addTo ? (
          <Link key={i} to={addTo} tabIndex={-1} className="empty-shelf-slot" data-depth={depth}>
            {middle}
          </Link>
        ) : (
          <div key={i} className="empty-shelf-slot" data-depth={depth}>
            {depth === 0 ? middle : <><span className="empty-shelf-cover" />{lines}</>}
          </div>
        ),
      )}
    </div>
  )
}

function MissingArt({ code }: { code?: string }) {
  return (
    <div className="empty-missing" aria-hidden>
      <span className="empty-missing-cover" data-back />
      <span className="empty-missing-cover">
        <span className="empty-missing-mark">
          <IconQuestionMark size={26} stroke={2} />
        </span>
        {code && <span className="empty-missing-code">{code}</span>}
      </span>
    </div>
  )
}
