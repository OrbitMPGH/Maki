import { useState, type CSSProperties } from 'react'
import { Paper, Title, Tooltip } from '@mantine/core'
import type { MangaBakaTag } from '../../api/hooks'

/** MangaBaka's own relevance buckets, most-relevant first, each with the token it is drawn in. */
const TAG_BUCKETS: { key: string; label: string; token: string }[] = [
  { key: 'core', label: 'Core', token: 'danger' },
  { key: 'defining', label: 'Defining', token: 'watched' },
  { key: 'recurrent', label: 'Recurrent', token: 'ok' },
  { key: 'incidental', label: 'Incidental', token: 'neutral' },
]

// Some series (e.g. One Piece) carry ~90 low-relevance tags in a single bucket. Show a bucket's
// first slice and put the rest behind a button: a wall of ninety chips is not a list anyone reads,
// but it is also not the panel's place to decide they can't have them.
const MAX_TAGS_PER_BUCKET = 18

/**
 * A series' tags, grouped by how much they define it.
 *
 * The relevance is the point, so it is what carries the colour — but as a dot per chip and a rule
 * under each bucket's name rather than forty filled badges, which at this count read as confetti
 * and make the buckets harder to tell apart, not easier.
 */
export function DiscoverTags({ tags }: { tags: MangaBakaTag[] }) {
  const [expanded, setExpanded] = useState<string[]>([])

  return (
    <Paper withBorder radius="lg" p="lg">
      <Title order={3} fz={17}>
        Tags
      </Title>

      {TAG_BUCKETS.map((bucket) => {
        const inBucket = tags.filter((t) => t.weight === bucket.key)
        if (inBucket.length === 0) return null

        const open = expanded.includes(bucket.key)
        const shown = open ? inBucket : inBucket.slice(0, MAX_TAGS_PER_BUCKET)
        const overflow = inBucket.length - shown.length

        return (
          <div
            key={bucket.key}
            className="tag-bucket"
            style={{ '--bucket': `var(--${bucket.token})` } as CSSProperties}
          >
            <div className="tag-bucket-head">
              <span className="tag-bucket-name">{bucket.label}</span>
              <span className="tag-bucket-rule" />
              <span className="tag-bucket-count tnum">{inBucket.length}</span>
            </div>

            <div className="tag-chips">
              {shown.map((t) => {
                const chip = (
                  <span
                    className={t.isSpoiler ? 'tag-chip spoiler-tag' : 'tag-chip'}
                    tabIndex={t.isSpoiler ? 0 : undefined}
                  >
                    <i className="tag-dot" />
                    <span>{t.name}</span>
                  </span>
                )
                // Spoiler tags always get a tooltip hint; others only when described.
                const tip = t.isSpoiler
                  ? t.description
                    ? `Spoiler · ${t.description}`
                    : 'Spoiler - hover to reveal'
                  : t.description
                return tip ? (
                  <Tooltip
                    key={t.name}
                    label={tip}
                    withArrow
                    multiline
                    maw={320}
                    openDelay={200}
                    zIndex={1001}
                  >
                    {chip}
                  </Tooltip>
                ) : (
                  <span key={t.name}>{chip}</span>
                )
              })}

              {overflow > 0 && (
                <button type="button" className="tag-more" onClick={() => setExpanded((e) => [...e, bucket.key])}>
                  +{overflow} more
                </button>
              )}
              {open && inBucket.length > MAX_TAGS_PER_BUCKET && (
                <button
                  type="button"
                  className="tag-more"
                  onClick={() => setExpanded((e) => e.filter((k) => k !== bucket.key))}
                >
                  Show fewer
                </button>
              )}
            </div>
          </div>
        )
      })}
    </Paper>
  )
}
