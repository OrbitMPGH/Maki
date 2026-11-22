import { useState } from 'react'
import { Link } from 'react-router-dom'
import { Button, Text, Tooltip, UnstyledButton, VisuallyHidden } from '@mantine/core'
import { useElementSize } from '@mantine/hooks'
import { Trans, useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'
import { IconBook, IconCircleCheck, IconClock, IconLightning, IconPlayerPause } from '../../../lib/icons'
import type {
  BehaviourSeries,
  BingeHabit,
  FormatPace,
  LengthSweetSpot,
  ReadingBehaviour,
  RecommendationFilters,
} from '../../../api/hooks'
import { Panel } from '../../../components/ui/Panel'
import { formatDecimal, formatNumber, formatPercent, formatReadingTime } from '../../../format'

/** Closest two pinned covers may sit, in px, before the later one moves over. */
const PIN_GAP = 36

/** The "short, finished" cutoff when the reader's own lengths are not known. */
const DEFAULT_SHORT = 60

/**
 * The chapter cap for "finished, under N chapters": a quarter above the length the reader
 * finishes, to the next ten, so their typical finished series is inside it.
 */
function shortCutoff(length: LengthSweetSpot | null | undefined): number {
  if (!length) return DEFAULT_SHORT
  return Math.min(300, Math.max(20, Math.ceil((length.finished * 1.25) / 10) * 10))
}

/** Read in at most this share of days counts the reader as a binger rather than a follower. */
const BINGE_SHARE = 0.5

/**
 * The lengths the reader finishes against the ones they stall on, as two bars on one chapter scale.
 * Finished is green (done); stalled is neutral, since stalling is not a failure.
 */
function LengthBars({ length, finishedCount }: { length: LengthSweetSpot; finishedCount: string }) {
  const { t } = useLingui()
  const scale = Math.max(length.finished, length.stalled) * 1.1
  const finished = formatNumber(Math.round(length.finished))
  const stalled = formatNumber(Math.round(length.stalled))
  return (
    <div className="taste-length">
      <div className="taste-length-head">
        <Text size="sm" fw={600}>
          <Trans>The length you finish</Trans>
        </Text>
        <Text span className="taste-stops-finished tnum">
          <IconCircleCheck size={16} aria-hidden />
          {finishedCount}
        </Text>
      </div>
      {[
        { key: 'finished', label: t`You finish`, value: length.finished, text: finished, icon: IconCircleCheck },
        { key: 'stalled', label: t`You stall on`, value: length.stalled, text: stalled, icon: IconPlayerPause },
      ].map((row) => (
        <div key={row.key} className="taste-length-row" data-kind={row.key}>
          <span className="taste-length-label">
            <row.icon size={14} aria-hidden />
            {row.label}
          </span>
          <span className="taste-length-track" aria-hidden>
            <b style={{ width: `${Math.max(2, (row.value / scale) * 100)}%` }} />
          </span>
          <span className="taste-length-value tnum">
            <Trans>
              <b>{row.text}</b> chapters
            </Trans>
          </span>
        </div>
      ))}
    </div>
  )
}

/** Where in a series the reader puts it down: the stop histogram with the series pinned under it. */
function StopCurve({
  behaviour,
  onFilters,
}: {
  behaviour: ReadingBehaviour
  onFilters: (filters: RecommendationFilters) => void
}) {
  const { t } = useLingui()
  const cutoff = shortCutoff(behaviour.length)
  const cutoffText = formatNumber(cutoff)
  const { medianStopPoint, stopPointHistogram: buckets, abandoned, seriesFinished } = behaviour
  const max = Math.max(0, ...buckets)
  const median = medianStopPoint === null ? null : Math.min(buckets.length - 1, Math.floor(medianStopPoint * buckets.length))
  const stop = medianStopPoint === null ? null : formatPercent(medianStopPoint)
  const finished = plural(seriesFinished, { one: '# finished', other: '# finished' })
  const { ref: chart, width } = useElementSize()
  const gap = width > 0 ? (PIN_GAP / width) * 100 : 6

  let last = -Infinity
  const pins = [...abandoned]
    .sort((a, b) => a.measure - b.measure)
    .map((item) => {
      const at = Math.max(item.measure * 100, last + gap)
      last = at
      return { item, at: Math.min(at, 96) }
    })

  return (
    <Panel className="taste-read-panel">
      <div className="taste-read-head">
        <p className="taste-big-line">
          {stop === null ? (
            <span className="taste-big">
              <Trans>You finish what you start</Trans>
            </span>
          ) : (
            <>
              <span className="taste-big tnum">
                <Trans>{stop} in</Trans>
              </span>{' '}
              <span className="taste-big-caption">
                <Trans>is where you usually stop</Trans>
              </span>
            </>
          )}
        </p>
        <Button
          variant="default"
          size="compact-sm"
          leftSection={<IconBook size={14} />}
          onClick={() => onFilters({ statuses: ['completed'], maxChapters: cutoff })}
        >
          {behaviour.length ? <Trans>Finished, under {cutoffText} chapters</Trans> : <Trans>Short, finished series</Trans>}
        </Button>
      </div>

      {max > 0 && median !== null && (
        <div ref={chart} className="taste-stops" role="img" aria-label={t`Where you stop in series you did not finish`}>
          <div className="taste-stops-bars">
            {buckets.map((count, i) => {
              const from = formatPercent(i / buckets.length)
              const to = formatPercent((i + 1) / buckets.length)
              const label = plural(count, {
                one: `# series stopped between ${from} and ${to}`,
                other: `# series stopped between ${from} and ${to}`,
              })
              return (
                <Tooltip key={i} label={label} withArrow>
                  <span
                    className="taste-stops-bar"
                    data-median={i === median || undefined}
                    style={{ height: `${Math.max(3, (count / max) * 100)}%` }}
                  >
                    {i === median && (
                      <span className="taste-stops-median">
                        <Trans>Your median</Trans>
                      </span>
                    )}
                  </span>
                </Tooltip>
              )
            })}
          </div>
          <div className="taste-stops-axis">
            <span className="taste-stops-start">
              <Trans>Start</Trans>
            </span>
            {pins.map(({ item, at }) => (
              <Link
                key={item.seriesId}
                to={`/series/${item.seriesId}`}
                className="taste-stops-pin"
                style={{ left: `${at}%` }}
                title={item.title}
                aria-label={t`${item.title}, stopped ${formatPercent(item.measure)} in`}
              >
                <span className="taste-cover taste-stops-cover">
                  {item.coverUrl && <img src={item.coverUrl} alt="" loading="lazy" decoding="async" />}
                </span>
                <span className="tnum">{formatPercent(item.measure)}</span>
              </Link>
            ))}
          </div>
        </div>
      )}

      {behaviour.length ? (
        <LengthBars length={behaviour.length} finishedCount={finished} />
      ) : (
        seriesFinished > 0 && (
          <Text className="taste-stops-finished tnum">
            <IconCircleCheck size={16} aria-hidden />
            {finished}
          </Text>
        )
      )}
    </Panel>
  )
}

function CoverTrio({ title, icon: TrioIcon, items }: { title: string; icon: typeof IconClock; items: BehaviourSeries[] }) {
  if (items.length === 0) return null
  return (
    <div className="taste-trio">
      <Text size="sm" fw={600} className="taste-trio-title">
        <TrioIcon size={15} aria-hidden />
        {title}
      </Text>
      <div className="taste-trio-covers">
        {items.map((item) => (
          <Link key={item.seriesId} to={`/series/${item.seriesId}`} className="taste-trio-item" title={item.title}>
            <span className="taste-cover">
              {item.coverUrl && <img src={item.coverUrl} alt="" loading="lazy" decoding="async" />}
            </span>
            <span className="tnum">{formatReadingTime(item.measure)}</span>
            <VisuallyHidden>{item.title}</VisuallyHidden>
          </Link>
        ))}
      </div>
    </div>
  )
}

/**
 * Paged against long-strip pace, as two cells that are also the switch for which format the cover
 * trios compare within. A long strip is one tall page and takes longer than a manga chapter, so
 * pooled lists would name strips as slow and manga as fast whatever the reader's own pace.
 */
function FormatSwitch({
  formats,
  value,
  onChange,
}: {
  formats: FormatPace[]
  value: FormatPace['format']
  onChange: (format: FormatPace['format']) => void
}) {
  const { t } = useLingui()
  const names: Record<FormatPace['format'], string> = { paged: t`Paged`, strip: t`Long strip` }
  return (
    <div className="taste-format" role="group" aria-label={t`Reading format`}>
      {formats.map((f) => {
        const timed = plural(f.timedChapters, { one: '# timed chapter', other: '# timed chapters' })
        return (
          <UnstyledButton
            key={f.format}
            className="taste-format-cell"
            aria-pressed={f.format === value}
            onClick={() => onChange(f.format)}
          >
            <span className="taste-format-time tnum">{formatReadingTime(f.medianSeconds)}</span>
            <span className="taste-format-text">
              <span className="taste-format-name">{names[f.format]}</span>
              <span className="taste-format-count tnum">{timed}</span>
            </span>
          </UnstyledButton>
        )
      })}
    </div>
  )
}

function Pace({ behaviour }: { behaviour: ReadingBehaviour }) {
  const { t } = useLingui()
  const formats = behaviour.paceByFormat ?? null
  const [format, setFormat] = useState<FormatPace['format']>('paged')
  const current = formats?.find((f) => f.format === format) ?? null
  const savoured = current ? current.savoured : behaviour.savoured
  const devoured = current ? current.devoured : behaviour.devoured
  const { medianSecondsPerChapter: pace, timedChapters, biggestDayCount, readingDays, medianChaptersPerReadingDay } =
    behaviour
  const formattedTimed = formatNumber(timedChapters)
  const timedNote =
    timedChapters > 0
      ? plural(timedChapters, {
          one: `Pace is from ${formattedTimed} timed chapter; only the built-in reader records time.`,
          other: `Pace is from ${formattedTimed} timed chapters; only the built-in reader records time.`,
        })
      : null
  const hasLists = savoured.length > 0 || devoured.length > 0

  return (
    <Panel className="taste-read-panel">
      <p className="taste-big-line">
        {pace === null ? (
          <span className="taste-big-caption">
            <Trans>No chapter here carries a reading time, so there is no pace to report.</Trans>{' '}
            <Trans>Only the built-in reader records it.</Trans>
          </span>
        ) : (
          <>
            <span className="taste-big tnum">{formatReadingTime(pace)}</span>{' '}
            <span className="taste-big-caption">
              <Trans>a typical chapter</Trans>
            </span>
          </>
        )}
      </p>

      {formats && <FormatSwitch formats={formats} value={format} onChange={setFormat} />}

      {hasLists ? (
        <div className="taste-trios">
          <CoverTrio title={t`You slow down for`} icon={IconClock} items={savoured} />
          <CoverTrio title={t`You tear through`} icon={IconLightning} items={devoured} />
        </div>
      ) : (
        pace !== null && (
          <Text size="sm" c="var(--ink-3)" mt="md">
            <Trans>Not enough timed chapters yet.</Trans>
          </Text>
        )
      )}

      <div className="taste-read-figures">
        {biggestDayCount !== null && (
          <span>
            <b className="tnum">{formatNumber(biggestDayCount)}</b>
            <Trans>chapters, biggest day</Trans>
          </span>
        )}
        <span>
          <b className="tnum">{formatNumber(readingDays)}</b>
          <Trans>reading days</Trans>
        </span>
        {medianChaptersPerReadingDay !== null && (
          <span>
            <b className="tnum">{formatDecimal(medianChaptersPerReadingDay)}</b>
            <Trans>chapters a day</Trans>
          </span>
        )}
      </div>
      {timedNote && (
        <Text size="xs" c="var(--ink-4)" mt="sm">
          {timedNote}
        </Text>
      )}
    </Panel>
  )
}

/**
 * One series' reading as a bar of its chapters, cut into a block per day it was read on. The bar's
 * length is the chapters against the longest example, so a 28-chapter binge reads longer than a
 * 6-chapter one, and days without reading never show as empty space.
 */
function BingeRow({ example, longest }: { example: BingeHabit['examples'][number]; longest: number }) {
  const { t } = useLingui()
  const chapters = formatNumber(example.chapters)
  const weeks = Math.max(1, Math.round(example.spanDays / 7))
  const summary =
    example.activeDays <= 2
      ? plural(example.activeDays, { one: `${chapters} ch. in # day`, other: `${chapters} ch. in # days` })
      : plural(weeks, { one: `${chapters} ch. over # week`, other: `${chapters} ch. over # weeks` })
  return (
    <Link to={`/series/${example.seriesId}`} className="taste-binge-row">
      <span className="taste-cover taste-binge-cover" aria-hidden>
        {example.coverUrl && <img src={example.coverUrl} alt="" loading="lazy" decoding="async" />}
      </span>
      <span className="taste-binge-title">{example.title}</span>
      <span className="taste-binge-summary tnum">{summary}</span>
      <span className="taste-binge-days" role="img" aria-label={t`${example.title}: ${summary}`}>
        <span style={{ width: `${(example.chapters / longest) * 100}%` }}>
          {example.perDay.map((count, i) => (
            <i key={i} style={{ flexGrow: count }} />
          ))}
        </span>
      </span>
    </Link>
  )
}

/**
 * Whether the reader binges a series once it hooks them or follows it over weeks, with the series
 * that show it. Leads to completed series for a binger, ongoing ones for a follower.
 */
function Binge({ binge, onFilters }: { binge: BingeHabit; onFilters: (filters: RecommendationFilters) => void }) {
  const binger = binge.binged / binge.series >= BINGE_SHARE
  const binged = formatNumber(binge.binged)
  const total = formatNumber(binge.series)
  const longest = Math.max(1, ...binge.examples.map((e) => e.chapters))
  return (
    <Panel className="taste-binge">
      <div className="taste-binge-lead">
        <p className="taste-big-line">
          <span className="taste-big tnum">
            <Trans>
              {binged} of {total}
            </Trans>
          </span>{' '}
          <span className="taste-big-caption">
            <Trans>read in one or two days</Trans>
          </span>
        </p>
        <Text c="var(--ink-2)" mt="xs">
          {binger ? (
            <Trans>When a series hooks you, you binge it.</Trans>
          ) : (
            <Trans>You follow series over weeks rather than in one go.</Trans>
          )}
        </Text>
        <Button
          variant="default"
          size="compact-sm"
          leftSection={<IconBook size={14} />}
          mt="md"
          onClick={() => onFilters({ statuses: [binger ? 'completed' : 'releasing'] })}
        >
          {binger ? <Trans>Completed series to binge</Trans> : <Trans>Ongoing series to follow</Trans>}
        </Button>
      </div>
      <div className="taste-binge-rows">
        {binge.examples.map((example) => (
          <BingeRow key={example.seriesId} example={example} longest={longest} />
        ))}
        <span className="taste-binge-axis">
          <Trans>Each block is a day you read it, sized by the chapters read that day.</Trans>
        </span>
      </div>
    </Panel>
  )
}

/**
 * How the reader reads, as pictures: where they put series down and how long the ones they finish
 * are, their pace by format with the series they slow down for and tear through, and whether they
 * binge or follow. The Stats page keeps the full figures; this side leads somewhere.
 */
export function TasteHabits({
  behaviour,
  onFilters,
}: {
  behaviour: ReadingBehaviour
  onFilters: (filters: RecommendationFilters) => void
}) {
  return (
    <>
      <div className="taste-read">
        <StopCurve behaviour={behaviour} onFilters={onFilters} />
        <Pace behaviour={behaviour} />
      </div>
      {behaviour.binge && <Binge binge={behaviour.binge} onFilters={onFilters} />}
    </>
  )
}

export function StatsLink({ section, children }: { section: 'habits' | 'taste'; children: React.ReactNode }) {
  return (
    <Button component={Link} to={`/stats?section=${section}`} variant="subtle" size="compact-sm" color="var(--ink-2)">
      {children}
    </Button>
  )
}
