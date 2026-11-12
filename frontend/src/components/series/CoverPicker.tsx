import { type ReactNode, useEffect, useMemo, useState } from 'react'
import { Button, Group, NumberInput, SegmentedControl, Select, Stack, Text, TextInput } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { notifications } from '../../lib/toast'
import { IconBook, IconLink, IconPhoto } from '../../lib/icons'
import { useChapters, useSourceMappings, useSources } from '../../api/hooks'
import { pageUrl } from '../../api/reader'
import { type CoverPick, useCoverChapterPages, usePickSeriesCover } from '../../api/seriesMetadata'
import { useAuth } from '../../auth/AuthProvider'
import { chapterLabel } from './chapterTable'

type Mode = 'page' | 'source' | 'url'

/**
 * The poster's other origins beside an upload: a page of a downloaded chapter, a linked source's
 * cover, or (admins only) an image URL. Each one locks the poster like an upload does.
 */
export function CoverPicker({ seriesId }: { seriesId: number }) {
  const { t } = useLingui()
  const { me } = useAuth()
  const [mode, setMode] = useState<Mode>('page')
  const pick = usePickSeriesCover()

  const apply = (next: CoverPick) =>
    pick.mutate(
      { seriesId, pick: next },
      { onSuccess: () => notifications.show({ message: t`Poster replaced`, color: 'var(--ok)' }) },
    )

  const modes = [
    { value: 'page', label: <ModeLabel icon={<IconBook size={14} />} text={t`Chapter page`} /> },
    { value: 'source', label: <ModeLabel icon={<IconPhoto size={14} />} text={t`Source cover`} /> },
    ...(me?.isAdmin ? [{ value: 'url', label: <ModeLabel icon={<IconLink size={14} />} text={t`Image URL`} /> }] : []),
  ]

  return (
    <Stack gap="xs" mt={4}>
      <SegmentedControl size="xs" value={mode} onChange={(next) => setMode(next as Mode)} data={modes} />
      {mode === 'page' && <PagePick seriesId={seriesId} busy={pick.isPending} onPick={apply} />}
      {mode === 'source' && <SourcePick seriesId={seriesId} busy={pick.isPending} onPick={apply} />}
      {mode === 'url' && me?.isAdmin && <UrlPick busy={pick.isPending} onPick={apply} />}
    </Stack>
  )
}

function ModeLabel({ icon, text }: { icon: ReactNode; text: string }) {
  return (
    <Group gap={6} wrap="nowrap" justify="center">
      {icon}
      <span>{text}</span>
    </Group>
  )
}

interface PickProps {
  busy: boolean
  onPick: (pick: CoverPick) => void
}

function PagePick({ seriesId, busy, onPick }: PickProps & { seriesId: number }) {
  const { t } = useLingui()
  const { data: chapters } = useChapters(seriesId)
  const downloaded = useMemo(() => (chapters ?? []).filter((c) => c.hasFile), [chapters])
  const [chapterId, setChapterId] = useState<number | null>(null)
  const [page, setPage] = useState(1)
  const { data: pages } = useCoverChapterPages(seriesId, chapterId)
  const pageCount = pages?.pageCount ?? 0
  const [thumb, setThumb] = useState<string | null>(null)

  useEffect(() => {
    let live = true
    if (chapterId == null || pageCount === 0) {
      setThumb(null)
      return
    }
    void pageUrl(chapterId, Math.min(page, pageCount) - 1, true).then((url) => live && setThumb(url))
    return () => {
      live = false
    }
  }, [chapterId, page, pageCount])

  if (chapters && downloaded.length === 0) {
    return (
      <Text size="sm" c="var(--ink-3)">
        <Trans>No chapter of this series is downloaded yet.</Trans>
      </Text>
    )
  }

  return (
    <Group align="flex-end" gap="sm" wrap="nowrap">
      {thumb && (
        <img
          src={thumb}
          alt=""
          style={{ width: 56, borderRadius: 'var(--mantine-radius-sm)', border: '1px solid var(--border)', flexShrink: 0 }}
        />
      )}
      <Select
        style={{ flex: 1 }}
        size="xs"
        label={t`Chapter`}
        searchable
        data={downloaded.map((c) => ({ value: String(c.id), label: chapterLabel(c) }))}
        value={chapterId == null ? null : String(chapterId)}
        onChange={(next) => {
          setChapterId(next == null ? null : Number(next))
          setPage(1)
        }}
      />
      <NumberInput
        w={96}
        size="xs"
        label={t`Page`}
        value={page}
        onChange={(next) => setPage(typeof next === 'number' ? next : 1)}
        min={1}
        max={Math.max(pageCount, 1)}
        allowDecimal={false}
        allowNegative={false}
        disabled={pageCount === 0}
      />
      <Button
        size="xs"
        variant="default"
        loading={busy}
        disabled={chapterId == null || pageCount === 0}
        onClick={() => chapterId != null && onPick({ from: 'page', chapterId, page: Math.min(page, pageCount) - 1 })}
      >
        <Trans>Use this page</Trans>
      </Button>
    </Group>
  )
}

function SourcePick({ seriesId, busy, onPick }: PickProps & { seriesId: number }) {
  const { t } = useLingui()
  const { data: mappings } = useSourceMappings(seriesId)
  const { data: sources } = useSources()
  const usable = useMemo(() => {
    const byName = new Map((sources ?? []).map((s) => [s.name, s]))
    return (mappings ?? []).flatMap((m) => {
      const source = byName.get(m.sourceName)
      return m.enabled && source?.enabled && source.provider !== 'suwayomi'
        ? [{ value: String(m.id), label: source.displayName }]
        : []
    })
  }, [mappings, sources])
  const [mappingId, setMappingId] = useState<string | null>(null)
  const chosen = mappingId ?? usable[0]?.value ?? null

  if (mappings && sources && usable.length === 0) {
    return (
      <Text size="sm" c="var(--ink-3)">
        <Trans>No linked source can supply a cover.</Trans>
      </Text>
    )
  }

  return (
    <Group align="flex-end" gap="sm" wrap="nowrap">
      <Select style={{ flex: 1 }} size="xs" label={t`Source`} data={usable} value={chosen} onChange={setMappingId} allowDeselect={false} />
      <Button
        size="xs"
        variant="default"
        loading={busy}
        disabled={chosen == null}
        onClick={() => chosen != null && onPick({ from: 'source', mappingId: Number(chosen) })}
      >
        <Trans>Use its cover</Trans>
      </Button>
    </Group>
  )
}

function UrlPick({ busy, onPick }: PickProps) {
  const { t } = useLingui()
  const [url, setUrl] = useState('')
  const valid = /^https?:\/\/\S+$/i.test(url.trim())

  return (
    <Stack gap={4}>
      <Group align="flex-end" gap="sm" wrap="nowrap">
        <TextInput
          style={{ flex: 1 }}
          size="xs"
          label={t`Image URL`}
          placeholder="https://"
          value={url}
          onChange={(e) => setUrl(e.currentTarget.value)}
        />
        <Button size="xs" variant="default" loading={busy} disabled={!valid} onClick={() => onPick({ from: 'url', url: url.trim() })}>
          <Trans>Fetch</Trans>
        </Button>
      </Group>
      <Text size="xs" c="var(--ink-4)">
        <Trans>Maki downloads the image itself, from a public address only.</Trans>
      </Text>
    </Stack>
  )
}
