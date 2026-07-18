import { useEffect, useState, type ReactNode } from 'react'
import { Badge, Group, SimpleGrid, Stack, Text } from '@mantine/core'
import { animate, motion, useReducedMotion } from 'motion/react'
import type { RewindStats } from '../../api/hooks'

const MONTHS = [
  'January',
  'February',
  'March',
  'April',
  'May',
  'June',
  'July',
  'August',
  'September',
  'October',
  'November',
  'December',
]

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

function BigNumber({ value, suffix }: { value: number; suffix: string }) {
  const shown = useCountUp(value)
  return (
    <div>
      <Text className="rewind-big-number tnum">{shown.toLocaleString()}</Text>
      <Text className="rewind-big-suffix">{suffix}</Text>
    </div>
  )
}

const eyebrow = (text: string) => (
  <Text className="rewind-eyebrow" tt="uppercase">
    {text}
  </Text>
)

export interface RewindSlide {
  key: string
  node: ReactNode
}

/** Builds the intro slide deck; slides with nothing to show are skipped. */
export function buildSlides(stats: RewindStats, label: string): RewindSlide[] {
  const slides: RewindSlide[] = []
  const t = stats.totals

  slides.push({
    key: 'title',
    node: (
      <Stack align="center" gap="xs">
        <Reveal>{eyebrow('Mangarr Rewind')}</Reveal>
        <Reveal delay={0.25}>
          <Text className="rewind-title">Your {label}</Text>
        </Reveal>
        <Reveal delay={0.55}>
          <Text className="rewind-sub">Let's look back at what you read.</Text>
        </Reveal>
      </Stack>
    ),
  })

  if (t.chaptersRead > 0 || t.volumesRead > 0) {
    slides.push({
      key: 'read',
      node: (
        <Stack align="center" gap="xs">
          <Reveal>{eyebrow('You turned a lot of pages')}</Reveal>
          <Reveal delay={0.3}>
            <BigNumber
              value={t.chaptersRead > 0 ? t.chaptersRead : t.volumesRead}
              suffix={t.chaptersRead > 0 ? 'chapters read' : 'volumes read'}
            />
          </Reveal>
          {t.chaptersRead > 0 && t.volumesRead > 0 && (
            <Reveal delay={1.1}>
              <Text className="rewind-sub">…plus {t.volumesRead} whole volumes.</Text>
            </Reveal>
          )}
        </Stack>
      ),
    })
  }

  const busiest = [...stats.timeline].sort((a, b) => b.chaptersRead - a.chaptersRead)[0]
  if (busiest && busiest.chaptersRead > 0 && busiest.bucket.length === 7) {
    const monthName = MONTHS[Number(busiest.bucket.split('-')[1]) - 1]
    slides.push({
      key: 'busiest',
      node: (
        <Stack align="center" gap="xs">
          <Reveal>{eyebrow('Your busiest month')}</Reveal>
          <Reveal delay={0.3}>
            <Text className="rewind-title">{monthName}</Text>
          </Reveal>
          <Reveal delay={0.6}>
            <Text className="rewind-sub">{busiest.chaptersRead} chapters in one month.</Text>
          </Reveal>
        </Stack>
      ),
    })
  }

  if (stats.topRead.length > 0) {
    slides.push({
      key: 'top-read',
      node: (
        <Stack align="center" gap="sm">
          <Reveal>{eyebrow('Your most read series')}</Reveal>
          <Reveal delay={0.3}>
            <Text className="rewind-title">{stats.topRead[0].title}</Text>
          </Reveal>
          <Reveal delay={0.6}>
            <Text className="rewind-sub">{stats.topRead[0].count} chapters</Text>
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
    slides.push({
      key: 'genres',
      node: (
        <Stack align="center" gap="sm">
          <Reveal>{eyebrow('You kept coming back to')}</Reveal>
          <Reveal delay={0.3}>
            <Text className="rewind-title">{stats.topGenres[0].name}</Text>
          </Reveal>
          {(stats.topGenres.length > 1 || stats.topTags.length > 0) && (
            <Reveal delay={0.7}>
              <Group gap={8} justify="center" maw={480}>
                {stats.topGenres.slice(1, 6).map((g) => (
                  <Badge key={g.name} size="lg" variant="white" color="dark">
                    {g.name}
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
    slides.push({
      key: 'growth',
      node: (
        <Stack align="center" gap="lg">
          <Reveal>{eyebrow('Your library grew')}</Reveal>
          <Group gap={48} justify="center">
            {t.seriesAdded > 0 && (
              <Reveal delay={0.3}>
                <BigNumber value={t.seriesAdded} suffix="series added" />
              </Reveal>
            )}
            {t.chaptersDownloaded > 0 && (
              <Reveal delay={0.55}>
                <BigNumber value={t.chaptersDownloaded} suffix="chapters downloaded" />
              </Reveal>
            )}
          </Group>
        </Stack>
      ),
    })
  }

  if (stats.finished.length > 0) {
    slides.push({
      key: 'finished',
      node: (
        <Stack align="center" gap="sm">
          <Reveal>{eyebrow('Seen through to the end')}</Reveal>
          <Reveal delay={0.3}>
            <Text className="rewind-title">
              {stats.finished.length === 1
                ? stats.finished[0].title
                : `${stats.finished.length} series finished`}
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
    slides.push({
      key: 'dropped',
      node: (
        <Stack align="center" gap="sm">
          <Reveal>{eyebrow('Maybe next year')}</Reveal>
          <Reveal delay={0.3}>
            <Text className="rewind-title">
              {stats.dropped.length} series {stats.dropped.length === 1 ? 'is' : 'are'} waiting
            </Text>
          </Reveal>
          <Reveal delay={0.65}>
            <Stack gap={4} align="center">
              {stats.dropped.slice(0, 4).map((s) => (
                <Text key={s.title} className="rewind-list-line">
                  {s.title} <span className="rewind-dim">— stalled at ch {s.maxChapter}</span>
                </Text>
              ))}
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
        <Reveal>{eyebrow(`That was your ${label}`)}</Reveal>
        <Reveal delay={0.3}>
          <SimpleGrid cols={{ base: 2, sm: 3 }} spacing="xl" className="rewind-summary-grid">
            {(
              [
                [t.chaptersRead, 'chapters read'],
                [t.volumesRead, 'volumes read'],
                [t.chaptersDownloaded, 'downloaded'],
                [t.seriesAdded, 'series added'],
                [t.seriesFinished, 'finished'],
                [t.seriesDropped, 'dropped'],
              ] as const
            )
              .filter(([value]) => value > 0)
              .map(([value, name]) => (
                <div key={name}>
                  <Text className="rewind-summary-number tnum">{value.toLocaleString()}</Text>
                  <Text className="rewind-summary-label">{name}</Text>
                </div>
              ))}
          </SimpleGrid>
        </Reveal>
        <Reveal delay={0.7}>
          <Text className="rewind-sub">The full breakdown is waiting behind this slide.</Text>
        </Reveal>
      </Stack>
    ),
  })

  return slides
}
