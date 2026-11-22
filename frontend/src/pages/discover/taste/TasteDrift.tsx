import { Fragment, useLayoutEffect, useRef } from 'react'
import { Link } from 'react-router-dom'
import { Text } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'
import type { TasteDriftPoint } from '../../../api/hooks'
import { Panel } from '../../../components/ui/Panel'
import { TagChip } from '../../../components/ui/TagChip'
import { formatPercent } from '../../../format'

const TAGS = 3

/** The quarter after the server's last "2026 Q4" bucket, or null for a label it does not recognise. */
function nextQuarter(bucket: string): string | null {
  const match = /^(\d{4}) Q([1-4])$/.exec(bucket)
  if (!match) return null
  const year = Number(match[1])
  const quarter = Number(match[2])
  return quarter === 4 ? `${year + 1} Q1` : `${year} Q${quarter + 1}`
}

function Quarter({ point }: { point: TasteDriftPoint }) {
  const count = plural(point.seriesCount, { one: '# series', other: '# series' })
  const examples = point.examples?.length ? point.examples : point.example ? [point.example] : []
  return (
    <Panel className="taste-quarter">
      <div className="taste-quarter-head">
        <span className="taste-quarter-name tnum">{point.bucket}</span>
        <Text span size="sm" c="var(--ink-3)" className="tnum">
          {count}
        </Text>
      </div>
      {point.distinctiveTags.length > 0 && (
        <div className="taste-quarter-tags">
          {point.distinctiveTags.slice(0, TAGS).map((tag) => (
            <TagChip key={tag} dot="var(--neutral)" size="sm">
              {tag}
            </TagChip>
          ))}
        </div>
      )}
      <div className="taste-quarter-covers">
        {examples.map((m) => (
          <Link key={m.seriesId} to={`/series/${m.seriesId}`} className="taste-cover" title={m.title} aria-label={m.title}>
            {m.coverUrl && <img src={m.coverUrl} alt="" loading="lazy" decoding="async" />}
          </Link>
        ))}
      </div>
    </Panel>
  )
}

/**
 * Where the reader's taste has moved, one card per quarter with what set it apart and the series
 * that sat at its centre. Between two cards, how much of the earlier quarter carried over.
 * Scrolls sideways once there are more quarters than fit, and starts at the latest.
 */
export function TasteDrift({ drift }: { drift: TasteDriftPoint[] }) {
  const { t } = useLingui()
  const scroller = useRef<HTMLDivElement>(null)
  // Opens on the latest quarter, not on the placeholder after it.
  useLayoutEffect(() => {
    const el = scroller.current
    const items = el?.querySelectorAll<HTMLElement>('[role="listitem"]')
    const latest = items?.[items.length - 1]
    if (el && latest) el.scrollLeft = latest.offsetLeft + latest.offsetWidth - el.clientWidth
  }, [drift])
  const upcoming = drift.length > 0 ? nextQuarter(drift[drift.length - 1].bucket) : null

  return (
    <div ref={scroller} className="taste-drift" role="list" aria-label={t`Quarters`}>
      {drift.map((point, i) => {
        const previous = drift[i - 1]
        const same = formatPercent(point.similarityToPrevious)
        const toward = point.distinctiveTags[0]
        return (
          <Fragment key={point.bucket}>
            {previous && (
              <div className="taste-quarter-link" aria-hidden>
                <span className="taste-quarter-same tnum">{same}</span>
                <span>
                  <Trans>the same</Trans>
                </span>
                <span className="taste-quarter-line" />
                {toward && (
                  <span className="taste-quarter-toward">
                    <Trans>toward {toward}</Trans>
                  </span>
                )}
              </div>
            )}
            <div role="listitem" className="taste-quarter-item">
              <Quarter point={point} />
            </div>
          </Fragment>
        )
      })}
      {upcoming && (
        <>
          <div className="taste-quarter-link taste-quarter-link-next" aria-hidden>
            <span className="taste-quarter-line" />
          </div>
          <div className="taste-quarter-item taste-quarter-next" aria-hidden>
            <span className="taste-quarter-name tnum">{upcoming}</span>
            <Text size="sm" c="var(--ink-3)">
              <Trans>Fills in as you read.</Trans>
            </Text>
          </div>
        </>
      )}
    </div>
  )
}
