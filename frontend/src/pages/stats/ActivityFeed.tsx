import { useMemo, useState } from 'react'
import { Anchor, Card, Group, Stack, Text, ThemeIcon } from '@mantine/core'
import {
  IconChecks,
  IconClockPause,
  IconPlus,
  IconTrash,
  type Icon,
} from '@tabler/icons-react'
import type { ActivityStats } from '../../api/hooks'
import { SeriesLink, SeriesThumb } from './SeriesLink'

const PAGE = 20

type FeedKind = 'finished' | 'added' | 'removed' | 'dropped'

interface FeedEntry {
  kind: FeedKind
  seriesId: number | null
  title: string
  coverUrl: string | null
  at: string
  note?: string
}

const KIND: Record<FeedKind, { label: string; icon: Icon; color: string }> = {
  finished: { label: 'Finished', icon: IconChecks, color: 'var(--ok)' },
  added: { label: 'Added', icon: IconPlus, color: 'var(--info)' },
  removed: { label: 'Removed', icon: IconTrash, color: 'var(--danger)' },
  dropped: { label: 'Stalled', icon: IconClockPause, color: 'var(--warn)' },
}

/**
 * Everything that happened to the library in the window, in one chronological list.
 *
 * Replaces four separate cards. They each went empty independently, so a quiet month left a row of
 * headed boxes saying nothing, and a busy one buried the order things actually happened in.
 */
export function ActivityFeed({ stats }: { stats: ActivityStats }) {
  const [expanded, setExpanded] = useState(false)

  const entries = useMemo<FeedEntry[]>(() => {
    const all: FeedEntry[] = [
      ...stats.finished.map((e) => ({ kind: 'finished' as const, ...e })),
      ...stats.added.map((e) => ({ kind: 'added' as const, ...e })),
      ...stats.removed.map((e) => ({ kind: 'removed' as const, ...e })),
      ...stats.dropped.map((d) => ({
        kind: 'dropped' as const,
        seriesId: d.seriesId,
        title: d.title,
        coverUrl: d.coverUrl,
        at: d.lastProgressAt,
        note: `ch ${d.maxChapter}`,
      })),
    ]
    return all.sort((a, b) => b.at.localeCompare(a.at))
  }, [stats])

  if (entries.length === 0) {
    return null
  }

  const shown = expanded ? entries : entries.slice(0, PAGE)

  return (
    <Card padding="md" radius="lg" withBorder>
      <Stack gap={2}>
        {shown.map((e, i) => {
          const kind = KIND[e.kind]
          const KindIcon = kind.icon
          return (
            <Group key={`${e.kind}-${e.title}-${i}`} gap={10} wrap="nowrap" py={4}>
              <ThemeIcon
                size={26}
                radius="md"
                variant="light"
                style={{ color: kind.color, background: `color-mix(in srgb, ${kind.color} 14%, transparent)` }}
              >
                <KindIcon size={15} />
              </ThemeIcon>
              <SeriesThumb url={e.coverUrl} alt={e.title} />
              <div style={{ flex: 1, minWidth: 0 }}>
                <Text size="sm" truncate>
                  <SeriesLink id={e.seriesId} title={e.title} />
                </Text>
                <Text size="xs" c="dimmed">
                  {kind.label}
                  {e.note ? ` · ${e.note}` : ''}
                </Text>
              </div>
              <Text size="xs" c="dimmed" className="tnum" style={{ flexShrink: 0 }}>
                {new Date(e.at).toLocaleDateString()}
              </Text>
            </Group>
          )
        })}
      </Stack>
      {entries.length > PAGE && (
        <Anchor component="button" type="button" size="xs" mt="sm" onClick={() => setExpanded((v) => !v)}>
          {expanded ? 'Show less' : `Show all ${entries.length}`}
        </Anchor>
      )}
    </Card>
  )
}
