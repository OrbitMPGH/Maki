import { Tooltip, VisuallyHidden } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { msg, plural } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import type { ScoreStanding } from '../../api/hooks'
import { formatFixedDecimal } from '../../format'
import { useLabel } from '../../i18n-context'
import { IconStarFilled } from '../../lib/icons'

/**
 * A word for a score, on fixed bands so the same score always gets the same word whatever the
 * series' type or the day's dump. Positive words only: below 7 the word stays out and the standing
 * line says the rest, so a series someone loves is never labelled "Low".
 */
export function scoreWord(score: number): MessageDescriptor | null {
  if (score >= 85) return msg({ message: 'Exceptional', context: 'score' })
  if (score >= 80) return msg({ message: 'Great', context: 'score' })
  if (score >= 70) return msg({ message: 'Good', context: 'score' })
  return null
}

/**
 * MangaBaka's 0-100 aggregate as a score out of ten with its word, where it stands among popular
 * series of its type, and that group's spread from 5 to 10 with this series marked in gold. Scores
 * cluster tightly, so the standing is what tells an 8.0 from a 7.4; the plain ring this replaced
 * looked three-quarters full on nearly every series.
 */
export function ScoreFigure({ score, standing }: { score: number; standing: ScoreStanding | null | undefined }) {
  const { t, i18n } = useLingui()
  const renderLabel = useLabel()
  const value = formatFixedDecimal(score / 10, 1)
  const word = scoreWord(score)
  const groupSize = standing?.groupSize ?? 0
  const top = (standing?.percentile ?? 0) >= 50
  const share = standing ? Math.max(1, Math.round(top ? 100 - standing.percentile : standing.percentile)) : 0
  const pct = i18n.number(share / 100, { style: 'percent' })
  const tooltip = standing
    ? standing.group === 'all'
      ? `${t`MangaBaka score, ${value} out of 10.`} ${plural(groupSize, {
          one: 'Compared with the # most popular series.',
          other: 'Compared with the # most popular series.',
        })}`
      : `${t`MangaBaka score, ${value} out of 10.`} ${plural(groupSize, {
          one: 'Compared with the # most popular series of its type.',
          other: 'Compared with the # most popular series of its type.',
        })}`
    : t`MangaBaka score, ${value} out of 10.`

  return (
    <div className="score-figure">
      <Tooltip label={tooltip} withArrow multiline w={280} zIndex={1001}>
        <div className="score-figure-text" tabIndex={0}>
          <span className="score-figure-head">
            <span className="score-figure-n figure">
              <IconStarFilled size={16} aria-hidden />
              {value}
              <small className="hero-stat-unit" aria-hidden>
                /10
              </small>
              <VisuallyHidden>
                <Trans>out of 10</Trans>
              </VisuallyHidden>
            </span>
            {word && <span className="score-figure-word">{renderLabel(word)}</span>}
          </span>
          {standing && <span className="hero-stat-l">{t(standingMessage(standing.group, top, pct))}</span>}
        </div>
      </Tooltip>
      {standing && <ScoreDistribution score={score} histogram={standing.histogram} />}
    </div>
  )
}

/**
 * One whole sentence per group and direction: "of popular manhwa" cannot be put together from the
 * type labels, which are standalone nouns, and translators need the sentence to place the number.
 */
function standingMessage(group: string, top: boolean, pct: string): MessageDescriptor {
  switch (group) {
    case 'manga':
      return top ? msg`Top ${pct} of popular manga` : msg`Bottom ${pct} of popular manga`
    case 'manhwa':
      return top ? msg`Top ${pct} of popular manhwa` : msg`Bottom ${pct} of popular manhwa`
    case 'manhua':
      return top ? msg`Top ${pct} of popular manhua` : msg`Bottom ${pct} of popular manhua`
    case 'oel':
      return top ? msg`Top ${pct} of popular OEL titles` : msg`Bottom ${pct} of popular OEL titles`
    case 'other':
      return top ? msg`Top ${pct} of other popular titles` : msg`Bottom ${pct} of other popular titles`
    default:
      return top ? msg`Top ${pct} of popular series` : msg`Bottom ${pct} of popular series`
  }
}

/**
 * The comparison group's scores in half-point bins from 5 to 10, bar heights true to the counts so
 * the cluster reads as a cluster. The series is a full-height gold line with a caret under the axis,
 * so its place does not rest on colour alone. The text beside it carries everything a screen reader
 * needs, so the chart itself is hidden from one.
 */
function ScoreDistribution({ score, histogram }: { score: number; histogram: number[] }) {
  const peak = Math.max(1, ...histogram)
  const at = Math.min(1, Math.max(0, (score - 50) / 50)) * 100
  return (
    <div className="score-dist" aria-hidden>
      <div className="score-dist-bars">
        {histogram.map((count, i) => (
          <span key={i} className="score-dist-bar" style={{ height: `${(count / peak) * 100}%` }} />
        ))}
        <span className="score-dist-marker" style={{ left: `${at}%` }} />
      </div>
      <div className="score-dist-axis">
        <span>5</span>
        <span className="score-dist-caret" style={{ left: `${at}%` }} />
        <span>10</span>
      </div>
    </div>
  )
}
