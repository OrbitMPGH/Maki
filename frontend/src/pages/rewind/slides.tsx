import { useEffect, useState, type ReactNode } from 'react'
import { Badge, Group, SimpleGrid, Stack, Text } from '@mantine/core'
import { animate, motion, useReducedMotion } from 'motion/react'
import { Trans, Plural } from '@lingui/react/macro'
import type { ActivityStats } from '../../api/hooks'
import { formatNumber, formatReadingTime, monthName } from '../../format'
import { GENRE_LABELS } from '../../components/CatalogueFilters'
import { useLabel } from '../../i18n-context'

/** Staggered fade-up wrapper used by every slide; collapses to instant cuts under reduced motion. */
function Reveal({ children, delay = 0 }: { children: ReactNode; delay?: number }) {
  const reduced = useReducedMotion()
  return (
    <motion.div
      initial={reduced ? false : { opacity: 0, y: 26 }}
      animate={{ opacity: 1, y: 0 }}
      transition={{ delay, duration: 0.55, ease: 'easeOut' }}
    >
      {children}
    </motion.div>
  )
}

function useCountUp(target: number): number {
  const reduced = useReducedMotion()
  const [value, setValue] = useState(reduced ? target : 0)
  useEffect(() => {
    if (reduced) {
      setValue(target)
      return
    }
    const controls = animate(0, target, {
      duration: 1.6,
      ease: 'easeOut',
      onUpdate: (v) => setValue(Math.round(v)),
    })
    return () => controls.stop()
  }, [target, reduced])
  return value
}

function BigNumber({ value, suffix }: { value: number; suffix: ReactNode }) {
  const shown = useCountUp(value)
  return (
    <div>
      <Text className="rewind-big-number tnum">{formatNumber(shown)}</Text>
      <Text className="rewind-big-suffix">{suffix}</Text>
    </div>
  )
}

const eyebrow = (text: ReactNode) => (
  <Text className="rewind-eyebrow" tt="uppercase">
    {text}
  </Text>
)

/** Genre names are wire values matched against the central `GENRE_LABELS` table; a tag has no such
 * table and is rendered as-is, same split as `TasteTab`'s composition cards. */
function GenreLabel({ name }: { name: string }) {
  const renderLabel = useLabel()
  return <>{renderLabel(GENRE_LABELS[name] ?? name)}</>
}

export interface RewindSlide {
  key: string
  node: ReactNode
}

/** Builds the intro slide deck; slides with nothing to show are skipped. */
export function buildSlides(stats: ActivityStats, label: string): RewindSlide[] {
  const slides: RewindSlide[] = []
  const t = stats.totals

  slides.push({
    key: 'title',
    node: (
      <Stack align="center" gap="xs">
        <Reveal>{eyebrow('Maki Rewind')}</Reveal>
        <Reveal delay={0.25}>
          <Text className="rewind-title">
            <Trans>Your {label}</Trans>
          </Text>
        </Reveal>
        <Reveal delay={0.55}>
          <Text className="rewind-sub">
            <Trans>Let's look back at what you read.</Trans>
          </Text>
        </Reveal>
      </Stack>
    ),
  })

  if (t.chaptersRead > 0 || t.volumesRead > 0) {
    const readValue = t.chaptersRead > 0 ? t.chaptersRead : t.volumesRead
    const volumesRead = t.volumesRead
    slides.push({
      key: 'read',
      node: (
        <Stack align="center" gap="xs">
          <Reveal>{eyebrow(<Trans>You turned a lot of pages</Trans>)}</Reveal>
          <Reveal delay={0.3}>
            <BigNumber
              value={readValue}
              suffix={
                t.chaptersRead > 0 ? (
                  <Plural value={readValue} one="chapter read" other="chapters read" />
                ) : (
                  <Plural value={readValue} one="volume read" other="volumes read" />
                )
              }
            />
          </Reveal>
          {t.chaptersRead > 0 && t.volumesRead > 0 && (
            <Reveal delay={1.1}>
              <Text className="rewind-sub">
                <Plural value={volumesRead} one="…plus # whole volume." other="…plus # whole volumes." />
              </Text>
            </Reveal>
          )}
        </Stack>
      ),
    })
  }

  if (t.readingSeconds > 0) {
    const topByTime = stats.topByTime[0]
    const topByTimeSpent = topByTime ? formatReadingTime(topByTime.seconds) : ''
    const topByTimeTitle = topByTime ? topByTime.title : ''
    slides.push({
      key: 'time',
      node: (
        <Stack align="center" gap="xs">
          <Reveal>{eyebrow(<Trans>Time spent in the reader</Trans>)}</Reveal>
          <Reveal delay={0.3}>
            <Text className="rewind-title">{formatReadingTime(t.readingSeconds)}</Text>
          </Reveal>
          {stats.topByTime.length > 0 && (
            <Reveal delay={0.7}>
              <Text className="rewind-sub">
                <Trans>
                  {topByTimeSpent} of it on {topByTimeTitle}.
                </Trans>
              </Text>
            </Reveal>
          )}
        </Stack>
      ),
    })
  }

  const busiest = [...stats.timeline].sort((a, b) => b.chaptersRead - a.chaptersRead)[0]
  if (busiest && busiest.chaptersRead > 0 && busiest.bucket.length === 7) {
    const busiestMonth = monthName(Number(busiest.bucket.split('-')[1]))
    const busiestChapters = busiest.chaptersRead
    slides.push({
      key: 'busiest',
      node: (
        <Stack align="center" gap="xs">
          <Reveal>{eyebrow(<Trans>Your busiest month</Trans>)}</Reveal>
          <Reveal delay={0.3}>
            <Text className="rewind-title">{busiestMonth}</Text>
          </Reveal>
          <Reveal delay={0.6}>
            <Text className="rewind-sub">
              <Plural
                value={busiestChapters}
                one="# chapter in one month."
                other="# chapters in one month."
              />
            </Text>
          </Reveal>
        </Stack>
      ),
    })
  }

  if (stats.topRead.length > 0) {
    const topReadTitle = stats.topRead[0].title
    const topReadCount = stats.topRead[0].count
    slides.push({
      key: 'top-read',
      node: (
        <Stack align="center" gap="sm">
          <Reveal>{eyebrow(<Trans>Your most read series</Trans>)}</Reveal>
          <Reveal delay={0.3}>
            <Text className="rewind-title">{topReadTitle}</Text>
          </Reveal>
          <Reveal delay={0.6}>
            <Text className="rewind-sub">
              <Plural value={topReadCount} one="# chapter" other="# chapters" />
            </Text>
          </Reveal>
          {stats.topRead.length > 1 && (
            <Reveal delay={0.95}>
              <Stack gap={4} mt="md" align="center">
                {stats.topRead.slice(1, 5).map((s, i) => (
                  <Text key={s.title} className="rewind-list-line">
                    <span className="rewind-rank tnum">{i + 2}</span> {s.title}
                  </Text>
                ))}
              </Stack>
            </Reveal>
          )}
        </Stack>
      ),
    })
  }

  if (stats.topGenres.length > 0) {
    const topGenreName = stats.topGenres[0].name
    slides.push({
      key: 'genres',
      node: (
        <Stack align="center" gap="sm">
          <Reveal>{eyebrow(<Trans>You kept coming back to</Trans>)}</Reveal>
          <Reveal delay={0.3}>
            <Text className="rewind-title">
              <GenreLabel name={topGenreName} />
            </Text>
          </Reveal>
          {(stats.topGenres.length > 1 || stats.topTags.length > 0) && (
            <Reveal delay={0.7}>
              <Group gap={8} justify="center" maw={480}>
                {stats.topGenres.slice(1, 6).map((g) => (
                  <Badge key={g.name} size="lg" variant="white" color="dark">
                    <GenreLabel name={g.name} />
                  </Badge>
                ))}
                {stats.topTags.slice(0, 4).map((tag) => (
                  <Badge key={tag.name} size="lg" variant="outline" color="gray.0">
                    {tag.name}
                  </Badge>
                ))}
              </Group>
            </Reveal>
          )}
        </Stack>
      ),
    })
  }

  if (t.seriesAdded > 0 || t.chaptersDownloaded > 0) {
    const seriesAdded = t.seriesAdded
    const chaptersDownloaded = t.chaptersDownloaded
    slides.push({
      key: 'growth',
      node: (
        <Stack align="center" gap="lg">
          <Reveal>{eyebrow(<Trans>Your library grew</Trans>)}</Reveal>
          <Group gap={48} justify="center">
            {seriesAdded > 0 && (
              <Reveal delay={0.3}>
                <BigNumber
                  value={seriesAdded}
                  suffix={<Plural value={seriesAdded} one="series added" other="series added" />}
                />
              </Reveal>
            )}
            {chaptersDownloaded > 0 && (
              <Reveal delay={0.55}>
                <BigNumber
                  value={chaptersDownloaded}
                  suffix={
                    <Plural
                      value={chaptersDownloaded}
                      one="chapter downloaded"
                      other="chapters downloaded"
                    />
                  }
                />
              </Reveal>
            )}
          </Group>
        </Stack>
      ),
    })
  }

  if (stats.finished.length > 0) {
    const finishedCount = stats.finished.length
    slides.push({
      key: 'finished',
      node: (
        <Stack align="center" gap="sm">
          <Reveal>{eyebrow(<Trans>Seen through to the end</Trans>)}</Reveal>
          <Reveal delay={0.3}>
            <Text className="rewind-title">
              {finishedCount === 1 ? (
                stats.finished[0].title
              ) : (
                <Plural value={finishedCount} one="# series finished" other="# series finished" />
              )}
            </Text>
          </Reveal>
          {stats.finished.length > 1 && (
            <Reveal delay={0.65}>
              <Stack gap={4} align="center">
                {stats.finished.slice(0, 5).map((s) => (
                  <Text key={s.title} className="rewind-list-line">
                    {s.title}
                  </Text>
                ))}
              </Stack>
            </Reveal>
          )}
        </Stack>
      ),
    })
  }

  if (stats.dropped.length > 0) {
    const droppedCount = stats.dropped.length
    slides.push({
      key: 'dropped',
      node: (
        <Stack align="center" gap="sm">
          <Reveal>{eyebrow(<Trans>Maybe next year</Trans>)}</Reveal>
          <Reveal delay={0.3}>
            <Text className="rewind-title">
              <Plural value={droppedCount} one="# series is waiting" other="# series are waiting" />
            </Text>
          </Reveal>
          <Reveal delay={0.65}>
            <Stack gap={4} align="center">
              {stats.dropped.slice(0, 4).map((s) => {
                const { title, maxChapter } = s
                return (
                  <Text key={title} className="rewind-list-line">
                    {title}{' '}
                    <span className="rewind-dim">
                      - <Trans>stalled at ch {maxChapter}</Trans>
                    </span>
                  </Text>
                )
              })}
            </Stack>
          </Reveal>
        </Stack>
      ),
    })
  }

  slides.push({
    key: 'summary',
    node: (
      <Stack align="center" gap="lg">
        <Reveal>{eyebrow(<Trans>That was your {label}</Trans>)}</Reveal>
        <Reveal delay={0.3}>
          <SimpleGrid cols={{ base: 2, sm: 3 }} spacing="xl" className="rewind-summary-grid">
            {(
              [
                // `shown` overrides the plain number for figures that are not counts. `id` is the
                // stable key: `label` below renders as translated markup and must never be one.
                { id: 'chapters-read', value: t.chaptersRead },
                { id: 'volumes-read', value: t.volumesRead },
                {
                  id: 'in-the-reader',
                  value: t.readingSeconds,
                  shown: formatReadingTime(t.readingSeconds),
                },
                { id: 'downloaded', value: t.chaptersDownloaded },
                { id: 'series-added', value: t.seriesAdded },
                { id: 'finished', value: t.seriesFinished },
                { id: 'dropped', value: t.seriesDropped },
              ] as const
            )
              .filter((entry) => entry.value > 0)
              .map((entry) => (
                <div key={entry.id}>
                  <Text className="rewind-summary-number tnum">
                    {'shown' in entry ? entry.shown : formatNumber(entry.value)}
                  </Text>
                  <Text className="rewind-summary-label">
                    <SummaryLabel id={entry.id} value={entry.value} />
                  </Text>
                </div>
              ))}
          </SimpleGrid>
        </Reveal>
        <Reveal delay={0.7}>
          <Text className="rewind-sub">
            <Trans>The full breakdown is waiting behind this slide.</Trans>
          </Text>
        </Reveal>
      </Stack>
    ),
  })

  return slides
}

/** The summary grid's per-entry noun, pluralised by that row's own count. */
function SummaryLabel({ id, value }: { id: string; value: number }) {
  switch (id) {
    case 'chapters-read':
      return <Plural value={value} one="chapter read" other="chapters read" />
    case 'volumes-read':
      return <Plural value={value} one="volume read" other="volumes read" />
    case 'in-the-reader':
      return <Trans>in the reader</Trans>
    case 'downloaded':
      return <Plural value={value} one="chapter downloaded" other="chapters downloaded" />
    case 'series-added':
      return <Plural value={value} one="series added" other="series added" />
    case 'finished':
      return <Plural value={value} one="series finished" other="series finished" />
    case 'dropped':
      return <Plural value={value} one="series dropped" other="series dropped" />
    default:
      return null
  }
}
