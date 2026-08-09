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
 * Collapses a series' add/remove churn to its net outcome.
 *
 * Removing and re-adding a series (by accident, or to force a re-scan) writes a real event every
 * time, and a series fiddled with a few times otherwise fills the whole feed with itself. Shows the
 * last thing that happened to it, dated to that event, with a count of the round trips.
 *
 * Display only: the events themselves are untouched and the headline tiles still count every one.
 * Keyed on seriesId when there is one, since a series removed and re-added shares an id only after
 * adoption has run; the title is what ties the halves together otherwise.
 */
function collapseChurn(entries: FeedEntry[]): FeedEntry[] {
  const bySeries = new Map<string, FeedEntry[]>()
  for (const e of entries) {
    const key = e.seriesId !== null ? `s${e.seriesId}` : `t${e.title.toLowerCase()}`
    const bucket = bySeries.get(key)
    if (bucket) {
      bucket.push(e)
    } else {
      bySeries.set(key, [e])
    }
  }

  const out: FeedEntry[] = []
  for (const group of bySeries.values()) {
    if (group.length === 1) {
      out.push(group[0])
      continue
    }

    const ordered = [...group].sort((a, b) => a.at.localeCompare(b.at))
    const latest = ordered[ordered.length - 1]
    // One round trip is an add and a remove, so pairs, not events.
    const cycles = Math.floor(ordered.length / 2)
    out.push({
      ...latest,
      note: cycles > 0 ? `re-added ${cycles}×` : undefined,
    })
  }

  return out
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
    const lifecycle = collapseChurn([
      ...stats.added.map((e) => ({ kind: 'added' as const, ...e })),
      ...stats.removed.map((e) => ({ kind: 'removed' as const, ...e })),
    ])

    const all: FeedEntry[] = [
      ...lifecycle,
      ...stats.finished.map((e) => ({ kind: 'finished' as const, ...e })),
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
