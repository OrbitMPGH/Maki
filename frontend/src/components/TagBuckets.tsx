import { useState, type CSSProperties, type ReactElement } from 'react'
import { Tooltip, VisuallyHidden } from '@mantine/core'
import { IconEyeOff } from '../lib/icons'
import { Trans, Plural, useLingui } from '@lingui/react/macro'
import { msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import type { MangaBakaTag } from '../api/hooks'
import { useLabel } from '../i18n-context'

/** MangaBaka's own relevance buckets, most-relevant first, each with the token it is drawn in. */
const TAG_BUCKETS: { key: string; label: MessageDescriptor; token: string }[] = [
  { key: 'core', label: msg`Core`, token: 'danger' },
  { key: 'defining', label: msg`Defining`, token: 'anime' },
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
  const [revealed, setRevealed] = useState<string[]>([])
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
        const plain = inBucket.filter((tag) => !tag.isSpoiler)
        const spoilers = inBucket.filter((tag) => tag.isSpoiler)
        const shown = open ? plain : plain.slice(0, MAX_TAGS_PER_BUCKET)
        const overflow = plain.length - shown.length
        const spoilersShown = revealed.includes(bucket.key)

        const withTip = (key: string, tip: string | null, chip: ReactElement) =>
          tip ? (
            <Tooltip key={key} label={tip} withArrow multiline maw={320} openDelay={200} zIndex={1001}>
              {chip}
            </Tooltip>
          ) : (
            <span key={key}>{chip}</span>
          )

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
              {shown.map(({ name, description }) =>
                withTip(
                  name,
                  description,
                  <span className="tag-chip">
                    <i className="tag-dot" />
                    <span>{name}</span>
                  </span>,
                ),
              )}

              {/* One placeholder per bucket, however many spoilers it holds; a click, tap or
                  Enter/Space shows them all and the same button hides them again. */}
              {spoilers.length > 0 && (
                <Tooltip
                  label={
                    spoilersShown ? (
                      <Plural value={spoilers.length} one="Spoiler tag. Click to hide it again." other="Spoiler tags. Click to hide them again." />
                    ) : (
                      <Plural value={spoilers.length} one="Spoiler tag. Click to reveal it." other="Spoiler tags. Click to reveal them." />
                    )
                  }
                  withArrow
                  multiline
                  maw={320}
                  openDelay={200}
                  zIndex={1001}
                >
                  <button
                    type="button"
                    className="tag-chip spoiler-tag"
                    data-interactive
                    data-revealed={spoilersShown || undefined}
                    aria-expanded={spoilersShown}
                    onClick={() =>
                      setRevealed((r) => (spoilersShown ? r.filter((k) => k !== bucket.key) : [...r, bucket.key]))
                    }
                  >
                    <i className="tag-dot" />
                    <IconEyeOff size={12} className="spoiler-tag-glyph" aria-hidden />
                    <span className="tnum">
                      <Plural value={spoilers.length} one="# spoiler tag" other="# spoiler tags" />
                    </span>
                  </button>
                </Tooltip>
              )}
              {spoilersShown &&
                spoilers.map(({ name, description }) =>
                  withTip(
                    name,
                    description ? t`Spoiler: ${description}` : null,
                    <span className="tag-chip spoiler-tag" data-revealed>
                      <i className="tag-dot" />
                      <VisuallyHidden>{t`Spoiler:`} </VisuallyHidden>
                      <span>{name}</span>
                    </span>,
                  ),
                )}

              {overflow > 0 && (
                <button type="button" className="tag-more" onClick={() => setExpanded((e) => [...e, bucket.key])}>
                  <Plural value={overflow} one="+# more" other="+# more" />
                </button>
              )}
              {open && plain.length > MAX_TAGS_PER_BUCKET && (
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
