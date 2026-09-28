import { Text } from '@mantine/core'
import { Link } from 'react-router-dom'
import { Select, Trans } from '@lingui/react/macro'
import type { CreatorReturnDto } from '../../api/stats'
import { formatNumber } from '../../format'

function initials(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean)
  if (parts.length === 0) return '?'
  const first = parts[0][0]
  const last = parts.length > 1 ? parts[parts.length - 1][0] : ''
  return (first + last).toUpperCase()
}

/** Creators the reader keeps coming back to: an initials avatar, their name, what they did, and how many series. */
export function CreatorList({ creators, max = 8 }: { creators: CreatorReturnDto[]; max?: number }) {
  const shown = creators.slice(0, max)
  return (
    <ul className="stats-rows">
      {shown.map((creator) => {
        const kind = creator.story && creator.art ? 'both' : creator.story ? 'story' : creator.art ? 'art' : 'none'
        return (
          <li className="stats-row" key={creator.name}>
            <div
              className="stats-thumb"
              aria-hidden
              style={{
                width: 36,
                height: 36,
                borderRadius: '50%',
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'center',
                fontWeight: 700,
                fontSize: 13,
                color: 'var(--ink-2)',
              }}
            >
              {initials(creator.name)}
            </div>
            <div style={{ minWidth: 0 }}>
              <Text
                component={Link}
                to={`/creator/${encodeURIComponent(creator.name)}`}
                className="stats-row-title"
                style={{ display: 'block' }}
              >
                {creator.name}
              </Text>
              <span className="stats-row-sub">
                <Trans>
                  <Select value={kind} _both="Story · Art" _story="Story" _art="Art" other="" />
                </Trans>
              </span>
            </div>
            <span className="stats-row-value">{formatNumber(creator.seriesRead)}</span>
          </li>
        )
      })}
    </ul>
  )
}
