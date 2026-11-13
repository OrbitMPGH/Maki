import { useEffect, useMemo, useRef, useState } from 'react'
import {
  
  Badge,
  Button,
  Checkbox,
  Group,
  Modal,
  ScrollArea,
  Stack,
  Text,
  
  Skeleton,
  UnstyledButton,
} from '@mantine/core'
import { SearchInput } from './ui/SearchInput'
import { IconCheck, IconFileZip } from '../lib/icons'
import { ModalFooter, ModalHeading } from './ui/ModalShell'
import { Panel } from './ui/Panel'
import { EmptyState } from './ui/EmptyState'
import { notifications } from '../lib/toast'
import { Trans, useLingui } from '@lingui/react/macro'
import { plural, t as now } from '@lingui/core/macro'
import { chapterLabel as houseChapterLabel, serverChapterLabel } from '../format'
import { useChapters, useLinkChapters, useUnlinkChapters } from '../api/hooks'
import type { ChapterDto, SeriesFileDto } from '../api/types'
import { displayChapterTitle } from '../lib/chapterTitle'

function chapterLabel(c: ChapterDto): string {
  if (!c.isOneShot && c.number === null && c.volume !== null) return serverChapterLabel(`Vol.${c.volume}`)
  if (c.isOneShot || c.number === null) return c.title ?? now`One-shot`
  return houseChapterLabel(c.number, c.volume)
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
  const { t, i18n } = useLingui()
  const opened = file !== null
  const { data: chapters, isLoading } = useChapters(seriesId)
  const link = useLinkChapters()
  const unlink = useUnlinkChapters()
  const [selected, setSelected] = useState<Set<number>>(new Set())
  const [initial, setInitial] = useState<Set<number>>(new Set())
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
    const onFile = new Set(chapters.filter((c) => c.filePath === file.relativePath).map((c) => c.id))
    setInitial(onFile)
    setSelected(new Set(onFile))
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
  }, [sorted, query, i18n.locale])

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

  const removed = useMemo(() => [...initial].filter((id) => !selected.has(id)), [initial, selected])
  const pending = link.isPending || unlink.isPending

  const confirm = async () => {
    if (!file) return
    const { fileName } = file
    const linkedPhrase = plural(selected.size, {
      one: '# chapter',
      other: '# chapters',
    })
    const unlinkedPhrase = plural(removed.length, {
      one: '# chapter',
      other: '# chapters',
    })
    try {
      if (removed.length > 0) {
        await unlink.mutateAsync(removed)
        setInitial(new Set(selected))
      }
      if (selected.size > 0)
        await link.mutateAsync({
          chapterIds: [...selected],
          relativePath: file.relativePath,
        })
    } catch {
      return
    }
    notifications.show({
      message:
        selected.size === 0
          ? now`Unlinked ${unlinkedPhrase} from ${fileName}`
          : removed.length > 0
            ? now`Linked ${linkedPhrase} to ${fileName} and unlinked ${unlinkedPhrase}`
            : now`Linked ${linkedPhrase} to ${fileName}`,
      color: 'var(--ok)',
    })
    onClose()
  }

  const missingCount = visible.filter((c) => !c.hasFile).length

  return (
    <Modal
      opened={opened}
      onClose={onClose}
      title={
        <ModalHeading
          title={t`Link chapters to this file`}
          description={
            <Trans>Tick the chapters this file contains. Shift-click for a range. Unticking one unlinks it.</Trans>
          }
        />
      }
      size="lg"
    >
      <Stack gap="sm">
        {file && (
          <Panel p="xs">
            <Group gap="xs" wrap="nowrap">
              <IconFileZip size={16} style={{ flexShrink: 0 }} />
              <span className="modal-mono" title={file.fileName}>
                {file.fileName}
              </span>
              {file.parsedLabel && (
                <Badge
                  size="sm"
                  variant="light"
                  color={file.isVolume ? 'var(--volume)' : 'var(--neutral)'}
                  className="tnum"
                >
                  {file.parsedLabel}
                </Badge>
              )}
            </Group>
          </Panel>
        )}
        {isLoading ? (
          <div className="modal-list" aria-busy="true" aria-label={t`Loading chapters…`}>
            {[52, 64, 40, 58].map((w) => (
              <div key={w} className="modal-list-row">
                <Skeleton h={16} w={16} />
                <Skeleton h={12} w={`${w}%`} />
              </div>
            ))}
          </div>
        ) : sorted.length === 0 ? (
          <EmptyState
            compact
            mood="asking"
            title={t`This series has no chapters yet. Add a source mapping first so Maki knows what to link.`}
          />
        ) : (
          <>
            <Group gap="xs" wrap="wrap">
              <SearchInput
                style={{ flex: 1, minWidth: 160 }}
                value={query}
                onChange={(e) => setQuery(e.currentTarget.value)}
                onClear={() => setQuery('')}
                placeholder={t`Filter by chapter number or title`}
                aria-label={t`Filter chapters`}
              />
              <Button
                size="xs"
                variant="default"
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
              <Button
                size="xs"
                variant="subtle"
                color="var(--neutral)"
                disabled={selected.size === 0}
                onClick={() => setSelected(new Set())}
              >
                <Trans>Clear</Trans>
              </Button>
            </Group>
            <ScrollArea.Autosize mah="min(400px, 45dvh)">
              <div className="modal-list">
                {visible.map((c) => {
                  const checked = selected.has(c.id)
                  const onOtherFile = c.hasFile && c.filePath !== file?.relativePath
                  const linkedHere = c.hasFile && !onOtherFile
                  return (
                    <UnstyledButton
                      key={c.id}
                      className="modal-list-row"
                      data-active={checked || undefined}
                      onClick={(e) => toggle(c.id, e.shiftKey)}
                      disabled={pending}
                      aria-pressed={checked}
                      style={{ userSelect: 'none' }}
                    >
                      <Checkbox.Indicator checked={checked} size="xs" />
                      <Text
                        size="sm"
                        fw={500}
                        c="var(--ink-hi)"
                        className="tnum"
                        style={{ whiteSpace: 'nowrap', minWidth: 56 }}
                      >
                        {chapterLabel(c)}
                      </Text>
                      {!c.isOneShot && displayChapterTitle(c) ? (
                        <Text size="sm" c="var(--ink-3)" truncate style={{ flex: 1, minWidth: 0 }}>
                          {displayChapterTitle(c)}
                        </Text>
                      ) : (
                        <span style={{ flex: 1 }} />
                      )}
                      {onOtherFile ? (
                        <Badge size="sm" variant="outline" color="var(--neutral)" style={{ flexShrink: 0 }}>
                          <Trans>On another file</Trans>
                        </Badge>
                      ) : linkedHere ? (
                        <Badge
                          size="sm"
                          variant="light"
                          color="var(--ok)"
                          leftSection={<IconCheck size={11} />}
                          style={{ flexShrink: 0 }}
                        >
                          <Trans>Linked here</Trans>
                        </Badge>
                      ) : (
                        <Badge size="sm" variant="light" color="var(--neutral)" style={{ flexShrink: 0 }}>
                          <Trans>Missing</Trans>
                        </Badge>
                      )}
                    </UnstyledButton>
                  )
                })}
                {visible.length === 0 && (
                  <EmptyState compact mood="asleep" title={t`No chapters match that filter.`} />
                )}
              </div>
            </ScrollArea.Autosize>
          </>
        )}
      </Stack>
      <ModalFooter
        status={plural(selected.size, {
          one: '# chapter selected',
          other: '# chapters selected',
        })}
      >
        <Button variant="default" onClick={onClose} disabled={pending}>
          <Trans>Cancel</Trans>
        </Button>
        <Button disabled={selected.size === 0 && removed.length === 0} loading={pending} onClick={() => void confirm()}>
          {selected.size === 0 ? <Trans>Unlink</Trans> : <Trans>Link</Trans>}
        </Button>
      </ModalFooter>
    </Modal>
  )
}
