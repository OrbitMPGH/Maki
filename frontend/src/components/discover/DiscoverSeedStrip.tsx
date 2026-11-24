import { useMemo } from 'react'
import { Link } from 'react-router-dom'
import { Trans, useLingui } from '@lingui/react/macro'
import { useSeries } from '../../api/hooks'
import { activeSeries } from '../../lib/series'
import { formatNumber } from '../../format'
import { IconX } from '../../lib/icons'

/**
 * The library series a personalised rail was built from, as the covers themselves.
 *
 * <p>
 * The rail's own subtitle names them in prose ("Because you read A, B and C and 3 more"), which is
 * a list of strings about series the reader owns and could recognise on sight. This shows the
 * covers instead, and each one is a link back into the library.
 * </p>
 *
 * <p>
 * The rail carries MangaBaka ids, not series: the seeds come from the recommender, which has no
 * library rows in scope. They are resolved here against the library list every page already holds.
 * A seed that no longer resolves — removed from the library between the rail being cached and this
 * render — is dropped rather than shown as a gap.
 * </p>
 */
export function DiscoverSeedStrip({
  seedIds,
  label,
  selected,
  onSelect,
}: {
  seedIds: number[]
  label?: string
  /** With `onSelect`, the seed whose picks the rail is narrowed to; null shows them all. */
  selected?: number | null
  /** Makes each seed a toggle that narrows the rail to it, instead of a link to the series. */
  onSelect?: (seedId: number | null) => void
}) {
  const { t } = useLingui()
  const resolvedLabel = label ?? t`Because you read`
  const { data: library } = useSeries()

  const seeds = useMemo(() => {
    if (!library) return []
    const byMangaBaka = new Map(
      activeSeries(library).filter((s) => s.mangaBakaId != null).map((s) => [s.mangaBakaId as number, s]),
    )
    return seedIds.map((id) => byMangaBaka.get(id)).filter((s): s is NonNullable<typeof s> => s != null)
  }, [library, seedIds])

  if (seeds.length === 0) return null

  return (
    <div className="discover-seeds">
      <span className="discover-seeds-label">{resolvedLabel}</span>
      <div className="discover-seeds-row">
        {seeds.map((s) => {
          const body = (
            <>
              {s.coverUrl ? (
                <img src={s.coverUrl} alt="" loading="lazy" decoding="async" />
              ) : (
                <span className="discover-seed-blank" aria-hidden />
              )}
              <span className="discover-seed-body">
                <span className="discover-seed-title">{s.displayTitle}</span>
                <span className="discover-seed-meta">{progressOf(s)}</span>
              </span>
            </>
          )
          if (!onSelect) {
            return (
              <Link key={s.id} to={`/series/${s.id}`} className="discover-seed" title={s.displayTitle}>
                {body}
              </Link>
            )
          }
          const on = selected === s.mangaBakaId
          return (
            <button
              key={s.id}
              type="button"
              className="discover-seed"
              data-selectable
              aria-pressed={on}
              title={on ? t`Show picks from every series` : t`Only picks from ${s.displayTitle}`}
              onClick={() => onSelect(on ? null : (s.mangaBakaId as number))}
            >
              {body}
              {on && (
                <span className="discover-seed-clear" aria-hidden>
                  <IconX size={11} />
                </span>
              )}
            </button>
          )
        })}
      </div>
    </div>
  )
}

/**
 * How far through the seed the reader is, against what is on disk rather than the provider's
 * chapter count: the denominator has to be something the reader can actually reach.
 *
 * `readChapterCount` is null on a series that was never tracked, which must not render as zero
 * read — those seeds show nothing rather than a wrong number.
 */
function progressOf(s: { readChapterCount: number | null; chapterFileCount: number }) {
  if (s.readChapterCount == null) return null
  if (s.chapterFileCount > 0 && s.readChapterCount >= s.chapterFileCount) return <Trans>Caught up</Trans>
  const read = formatNumber(s.readChapterCount)
  const total = formatNumber(s.chapterFileCount)
  return (
    <Trans>
      Ch. {read} of {total}
    </Trans>
  )
}
