import type { ReactNode } from 'react'
import { Link } from 'react-router-dom'
import { Anchor, Text } from '@mantine/core'

/** One labelled fact, label beside value. Goes inside a `<dl className="detail-records">`. */
export function DetailRecord({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="detail-record">
      <Text component="dt" size="xs" c="var(--ink-3)">
        {label}
      </Text>
      <Text component="dd" size="sm" c="var(--ink)">
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
