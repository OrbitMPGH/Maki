import type { ReactNode } from 'react'
import { Link } from 'react-router-dom'
import { Anchor, Text } from '@mantine/core'

/**
 * One labelled fact. Label over value by default, which is what fits the rail; a `.detail-records`
 * wrapper marked `data-wide` puts the two on one line for the full-width panels.
 */
export function DetailRecord({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="detail-record">
      <Text size="xs" c="var(--ink-4)">
        {label}
      </Text>
      <Text size="sm" c="var(--ink-2)">
        {children}
      </Text>
    </div>
  )
}

/**
 * Names that link through to everything that person or studio made.
 *
 * <p>
 * Navigating closes the modal first. On Discover it can already be the second layer, opened from
 * behind `FeedExpandModal` and carrying an explicit zIndex to survive that; a creator view stacked
 * on top would be a third. A route is also linkable, which a modal is not, and Back brings the
 * results underneath straight out of the query cache.
 * </p>
 */
export function Creators({
  values,
  role,
  onNavigate,
}: {
  values: string[]
  role: 'author' | 'artist' | 'studio'
  onNavigate: () => void
}) {
  return (
    <>
      {values.map((value, i) => (
        <span key={value}>
          {i > 0 && ', '}
          <Anchor
            component={Link}
            to={`/creator/${encodeURIComponent(value)}?role=${role}`}
            onClick={onNavigate}
            inherit
          >
            {value}
          </Anchor>
        </span>
      ))}
    </>
  )
}
