import { useState, type CSSProperties } from 'react'
import { Tooltip } from '@mantine/core'
import { Trans, Plural, useLingui } from '@lingui/react/macro'
import { msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import type { MangaBakaTag } from '../api/hooks'
import { useLabel } from '../i18n-context'

/** MangaBaka's own relevance buckets, most-relevant first, each with the token it is drawn in. */
const TAG_BUCKETS: { key: string; label: MessageDescriptor; token: string }[] = [
  { key: 'core', label: msg`Core`, token: 'danger' },
  { key: 'defining', label: msg`Defining`, token: 'watched' },
  { key: 'recurrent', label: msg`Recurrent`, token: 'ok' },
  { key: 'incidental', label: msg`Incidental`, token: 'neutral' },
]

const KNOWN_WEIGHTS = new Set(TAG_BUCKETS.map((b) => b.key))

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
 *
 * Bare markup with no panel around it, so callers can drop it into their own card
 * (`DiscoverTags` gives it one; the series page's Details panel puts it under a field label).
 */
export function TagBuckets({ tags }: { tags: MangaBakaTag[] }) {
  const [expanded, setExpanded] = useState<string[]>([])
  const { t } = useLingui()
  const label = useLabel()

  // Anything MangaBaka weighted outside the four known buckets still gets shown, in a plain
  // trailing group. Rare, but dropping tags on the floor is worse than an unlabelled colour.
  const otherCount = tags.filter((tag) => !KNOWN_WEIGHTS.has(tag.weight)).length
  const buckets =
    otherCount > 0 ? [...TAG_BUCKETS, { key: '', label: msg`Other`, token: 'neutral' }] : TAG_BUCKETS

  return (
    <>
      {buckets.map((bucket) => {
        const inBucket = bucket.key
          ? tags.filter((tag) => tag.weight === bucket.key)
          : tags.filter((tag) => !KNOWN_WEIGHTS.has(tag.weight))
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
              <span className="tag-bucket-name">{label(bucket.label)}</span>
              <span className="tag-bucket-rule" />
              <span className="tag-bucket-count tnum">{inBucket.length}</span>
            </div>

            <div className="tag-chips">
              {shown.map((tag) => {
                const { description, isSpoiler, name } = tag
                const chip = (
                  <span
                    className={isSpoiler ? 'tag-chip spoiler-tag' : 'tag-chip'}
                    tabIndex={isSpoiler ? 0 : undefined}
                  >
                    <i className="tag-dot" />
                    <span>{name}</span>
                  </span>
                )
                // Spoiler tags always get a tooltip hint; others only when described.
                const tip = isSpoiler
                  ? description
                    ? t`Spoiler · ${description}`
                    : t`Spoiler - hover to reveal`
                  : description
                return tip ? (
                  <Tooltip
                    key={name}
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
                  <span key={name}>{chip}</span>
                )
              })}

              {overflow > 0 && (
                <button type="button" className="tag-more" onClick={() => setExpanded((e) => [...e, bucket.key])}>
                  <Plural value={overflow} one="+# more" other="+# more" />
                </button>
              )}
              {open && inBucket.length > MAX_TAGS_PER_BUCKET && (
                <button
                  type="button"
                  className="tag-more"
                  onClick={() => setExpanded((e) => e.filter((k) => k !== bucket.key))}
                >
                  <Trans>Show fewer</Trans>
                </button>
              )}
            </div>
          </div>
        )
      })}
    </>
  )
}
