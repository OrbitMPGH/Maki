import { useEffect, useMemo, useRef, useState } from 'react'
import {
  ActionIcon,
  Badge,
  Button,
  Checkbox,
  Group,
  Loader,
  Modal,
  ScrollArea,
  Stack,
  Text,
  TextInput,
  UnstyledButton,
} from '@mantine/core'
import { IconFileZip, IconSearch, IconX } from '@tabler/icons-react'
import { notifications } from '@mantine/notifications'
import { Trans, useLingui } from '@lingui/react/macro'
import { plural, t as now } from '@lingui/core/macro'
import { useChapters, useLinkChapters } from '../api/hooks'
import type { ChapterDto, SeriesFileDto } from '../api/types'

function chapterLabel(c: ChapterDto): string {
  if (c.isOneShot || c.number === null) return c.title ?? now`One-shot`
  return c.volume !== null ? now`Vol.${c.volume} Ch.${c.number}` : now`Ch.${c.number}`
}

/**
 * The file-first direction of manual linking: the user has a file in the folder (an omnibus, a
 * badly named release) and picks which chapters live inside it. `LinkChaptersModal` is the
 * chapter-first direction; both call the same endpoint.
 */
export function LinkFileToChaptersModal({
  seriesId,
  file,
  onClose,
}: {
  seriesId: number
  file: SeriesFileDto | null
  onClose: () => void
}) {
  const { t } = useLingui()
  const opened = file !== null
  const { data: chapters, isLoading } = useChapters(seriesId)
  const link = useLinkChapters()
  const [selected, setSelected] = useState<Set<number>>(new Set())
  const [query, setQuery] = useState('')
  const anchor = useRef<number | null>(null)
  const seededFor = useRef<string | null>(null)

  // Chapters already on this file start checked so the dialog doubles as "what is in this file".
  // Seeded once per opened file, not on every chapters refetch, or a background refresh would
  // wipe ticks mid-edit.
  useEffect(() => {
    if (!file) {
      seededFor.current = null
      return
    }
    if (!chapters || seededFor.current === file.relativePath) return
    seededFor.current = file.relativePath
    setSelected(new Set(chapters.filter((c) => c.filePath === file.relativePath).map((c) => c.id)))
    setQuery('')
    anchor.current = null
  }, [file, chapters])

  const sorted = useMemo(() => {
    const rows = [...(chapters ?? [])]
    rows.sort((a, b) => (a.number ?? Infinity) - (b.number ?? Infinity))
    return rows
  }, [chapters])

  const visible = useMemo(() => {
    const q = query.trim().toLocaleLowerCase()
    if (!q) return sorted
    return sorted.filter(
      (c) =>
        chapterLabel(c).toLocaleLowerCase().includes(q) ||
        (c.title ?? '').toLocaleLowerCase().includes(q) ||
        (c.numberRaw ?? '').toLocaleLowerCase().includes(q),
    )
  }, [sorted, query])

  const toggle = (id: number, shift: boolean) => {
    setSelected((prev) => {
      const next = new Set(prev)
      if (shift && anchor.current !== null) {
        const ids = visible.map((c) => c.id)
        const from = ids.indexOf(anchor.current)
        const to = ids.indexOf(id)
        if (from !== -1 && to !== -1) {
          const [lo, hi] = from < to ? [from, to] : [to, from]
          const turnOn = !prev.has(id)
          for (const rowId of ids.slice(lo, hi + 1)) {
            if (turnOn) next.add(rowId)
            else next.delete(rowId)
          }
          return next
        }
      }
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })
    anchor.current = id
  }

  const confirm = () => {
    if (!file) return
    const { fileName } = file
    const chapterIds = [...selected]
    const chapterPhrase = plural(chapterIds.length, { one: '# chapter', other: '# chapters' })
    link.mutate(
      { chapterIds, relativePath: file.relativePath },
      {
        onSuccess: () => {
          notifications.show({
            message: now`Linked ${chapterPhrase} to ${fileName}`,
            color: 'var(--ok)',
          })
          onClose()
        },
      },
    )
  }

  const missingCount = visible.filter((c) => !c.hasFile).length

  return (
    <Modal opened={opened} onClose={onClose} title={t`Link chapters to this file`} size="lg">
      <Stack gap="sm">
        {file && (
          <Group gap="xs" wrap="nowrap">
            <IconFileZip size={16} style={{ flexShrink: 0 }} />
            <Text size="sm" fw={600} style={{ wordBreak: 'break-all' }}>
              {file.fileName}
            </Text>
            {file.parsedLabel && (
              <Badge size="sm" variant="light" color={file.isVolume ? 'indigo' : 'gray'} className="tnum">
                {file.parsedLabel}
              </Badge>
            )}
          </Group>
        )}
        <Text size="sm" c="var(--ink-3)">
          <Trans>
            Tick every chapter contained in this file. An omnibus or compilation usually covers a
            range: click the first chapter, then shift-click the last.
          </Trans>
        </Text>
        {isLoading ? (
          <Group py="md" gap="xs">
            <Loader size="sm" />
            <Text size="sm" c="var(--ink-3)">
              <Trans>Loading chapters…</Trans>
            </Text>
          </Group>
        ) : sorted.length === 0 ? (
          <Text c="var(--ink-3)" size="sm" py="sm">
            <Trans>This series has no chapters yet. Add a source mapping first so Maki knows what to link.</Trans>
          </Text>
        ) : (
          <>
            <Group gap="xs" wrap="wrap">
              <TextInput
                size="xs"
                style={{ flex: 1, minWidth: 160 }}
                value={query}
                onChange={(e) => setQuery(e.currentTarget.value)}
                leftSection={<IconSearch size={14} />}
                rightSection={
                  query ? (
                    <ActionIcon size="sm" variant="subtle" color="gray" aria-label={t`Clear search`} onClick={() => setQuery('')}>
                      <IconX size={12} />
                    </ActionIcon>
                  ) : null
                }
                placeholder={t`Filter by chapter number or title`}
                aria-label={t`Filter chapters`}
              />
              <Button
                size="xs"
                variant="subtle"
                disabled={missingCount === 0}
                onClick={() =>
                  setSelected((prev) => {
                    const next = new Set(prev)
                    for (const c of visible) if (!c.hasFile) next.add(c.id)
                    return next
                  })
                }
              >
                <Trans>Select missing ({missingCount})</Trans>
              </Button>
              <Button size="xs" variant="subtle" disabled={selected.size === 0} onClick={() => setSelected(new Set())}>
                <Trans>Clear</Trans>
              </Button>
            </Group>
            <ScrollArea.Autosize mah="min(400px, 45dvh)">
              <Stack gap={2}>
                {visible.map((c) => {
                  const checked = selected.has(c.id)
                  const onOtherFile = c.hasFile && c.filePath !== file?.relativePath
                  return (
                    <UnstyledButton
                      key={c.id}
                      onClick={(e) => toggle(c.id, e.shiftKey)}
                      disabled={link.isPending}
                      px="xs"
                      py={6}
                      aria-pressed={checked}
                      style={{
                        borderRadius: 'var(--radius-thumb)',
                        background: checked ? 'color-mix(in srgb, var(--brand) 12%, transparent)' : undefined,
                        userSelect: 'none',
                      }}
                    >
                      <Group justify="space-between" wrap="nowrap" gap="xs">
                        <Group gap="sm" wrap="nowrap" style={{ minWidth: 0 }}>
                          <Checkbox checked={checked} readOnly tabIndex={-1} size="xs" aria-hidden />
                          <Text size="sm" className="tnum" style={{ whiteSpace: 'nowrap' }}>
                            {chapterLabel(c)}
                          </Text>
                          {c.title && !c.isOneShot && (
                            <Text size="sm" c="var(--ink-3)" truncate>
                              {c.title}
                            </Text>
                          )}
                        </Group>
                        {onOtherFile ? (
                          <Badge size="sm" variant="outline" color="gray" style={{ flexShrink: 0 }}>
                            <Trans>On another file</Trans>
                          </Badge>
                        ) : !c.hasFile ? (
                          <Badge size="sm" variant="light" color="gray" style={{ flexShrink: 0 }}>
                            <Trans>Missing</Trans>
                          </Badge>
                        ) : null}
                      </Group>
                    </UnstyledButton>
                  )
                })}
                {visible.length === 0 && (
                  <Text size="sm" c="var(--ink-3)" py="sm">
                    <Trans>No chapters match that filter.</Trans>
                  </Text>
                )}
              </Stack>
            </ScrollArea.Autosize>
          </>
        )}
        <Group justify="space-between" mt="xs">
          <Text size="sm" c="var(--ink-3)" className="tnum">
            {plural(selected.size, { one: '# chapter selected', other: '# chapters selected' })}
          </Text>
          <Group gap="xs">
            <Button variant="default" onClick={onClose} disabled={link.isPending}>
              <Trans>Cancel</Trans>
            </Button>
            <Button disabled={selected.size === 0} loading={link.isPending} onClick={confirm}>
              <Trans>Link</Trans>
            </Button>
          </Group>
        </Group>
      </Stack>
    </Modal>
  )
}
