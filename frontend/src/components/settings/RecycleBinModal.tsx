import { useMemo, useState } from 'react'
import {
  ActionIcon,
  Button,
  Collapse,
  Group,
  Modal,
  Skeleton,
  Stack,
  Tabs,
  Text,
  
  Tooltip,
  UnstyledButton,
} from '@mantine/core'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { msg, plural, t as now } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { SearchInput } from '../ui/SearchInput'
import { IconChevronDown, IconRestore, IconTrash } from '../../lib/icons'
import { notifications } from '../../lib/toast'
import {
  useDeleteManyFromRecycleBin,
  useEmptyRecycleBin,
  useRecycleBin,
  useRestoreFromRecycleBin,
  useRestoreManyFromRecycleBin,
  type BulkResult,
  type RecycleBinEntry,
  type RecycleReason,
} from '../../api/recycleBin'
import { useSeries } from '../../api/hooks'
import { useLabel } from '../../i18n-context'
import { formatBytes, formatDate, formatNumber } from '../../format'
import { ConfirmDialog } from '../ui/ConfirmDialog'
import { EmptyState } from '../ui/EmptyState'
import { MetaList } from '../ui/MetaList'
import { Thumb } from '../ui/Thumb'
import { ModalFooter, ModalHeading } from '../ui/ModalShell'

const REASON_ORDER: RecycleReason[] = ['deleteFile', 'removeChapter', 'seriesDelete', 'seriesArchive']

const REASON_LABELS: Record<RecycleReason, MessageDescriptor> = {
  deleteFile: msg`Deleted file`,
  removeChapter: msg`Removed chapter`,
  seriesDelete: msg`Removed series`,
  seriesArchive: msg({ message: 'Series archived', context: 'series' }),
}

/** Files at or under this many days from deletion get the warning tone. */
const SOON_DAYS = 3
/** Larger groups start closed, so one removed series doesn't bury everything under it. */
const OPEN_UP_TO = 3

function daysLeft(expiresAt: string): number {
  return Math.max(0, Math.ceil((new Date(expiresAt).getTime() - Date.now()) / 86_400_000))
}

function chapterLabel(entry: RecycleBinEntry): string {
  const numbers = entry.chapters
    .map((c) => c.number)
    .filter((n): n is number => n !== null)
    .sort((a, b) => a - b)
  if (numbers.length === 0) return ''
  const first = formatNumber(numbers[0])
  if (numbers.length === 1) return now`Ch. ${first}`
  const last = formatNumber(numbers[numbers.length - 1])
  return now`Ch. ${first} to ${last}`
}

interface BinGroup {
  seriesId: number
  seriesTitle: string
  seriesExists: boolean
  entries: RecycleBinEntry[]
  size: number
  latest: number
  soonest: number
}

function groupBySeries(entries: RecycleBinEntry[]): BinGroup[] {
  const groups = new Map<number, BinGroup>()
  for (const entry of entries) {
    let group = groups.get(entry.seriesId)
    if (!group) {
      group = {
        seriesId: entry.seriesId,
        seriesTitle: entry.seriesTitle,
        seriesExists: entry.seriesExists,
        entries: [],
        size: 0,
        latest: 0,
        soonest: Infinity,
      }
      groups.set(entry.seriesId, group)
    }
    group.entries.push(entry)
    group.size += entry.size
    group.latest = Math.max(group.latest, new Date(entry.deletedAt).getTime())
    group.soonest = Math.min(group.soonest, daysLeft(entry.expiresAt))
  }
  for (const group of groups.values()) {
    group.entries.sort((a, b) => new Date(b.deletedAt).getTime() - new Date(a.deletedAt).getTime())
  }
  // Newest first: the usual visit is "I just deleted the wrong thing".
  return [...groups.values()].sort((a, b) => b.latest - a.latest)
}

function bulkToast(result: BulkResult, restored: boolean) {
  if (result.failed > 0) {
    notifications.show({
      color: 'var(--danger)',
      message: plural(result.failed, {
        one: "Couldn't finish # file, check the log",
        other: "Couldn't finish # files, check the log",
      }),
    })
  }
  if (result.done === 0) return
  notifications.show({
    color: 'var(--ok)',
    message: !restored
      ? plural(result.done, { one: 'Deleted # file for good', other: 'Deleted # files for good' })
      : result.unlinked > 0
        ? plural(result.unlinked, {
            one: 'Restored. # file came back unlinked, Health can import it.',
            other: 'Restored. # files came back unlinked, Health can import them.',
          })
        : plural(result.done, { one: 'Restored # file', other: 'Restored # files' }),
  })
}

/**
 * Files deleted from the library, waiting to be restored or purged, opened from the Settings card.
 * Grouped by series, newest deletion first, so one removed series is a single folded line.
 */
export function RecycleBinModal({ opened, onClose }: { opened: boolean; onClose: () => void }) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const { data: bin, isLoading, isError, refetch } = useRecycleBin(opened)
  const { data: series } = useSeries(opened)
  const restoreMany = useRestoreManyFromRecycleBin()
  const removeMany = useDeleteManyFromRecycleBin()
  const empty = useEmptyRecycleBin()

  const [reason, setReason] = useState<RecycleReason | null>(null)
  const [query, setQuery] = useState('')
  // Groups the user opened or closed, against each group's default.
  const [toggled, setToggled] = useState<Set<number>>(new Set())
  const [deleting, setDeleting] = useState<{ entries: RecycleBinEntry[]; title: string } | null>(null)
  const [emptying, setEmptying] = useState(false)

  const entries = useMemo(() => bin?.entries ?? [], [bin])
  const days = bin?.retentionDays ?? 14
  const totalSize = formatBytes(bin?.totalBytes)
  const covers = useMemo(() => new Map((series ?? []).map((s) => [s.id, s.coverUrl])), [series])

  const reasonCounts = useMemo(() => {
    const counts = new Map<RecycleReason, number>()
    for (const e of entries) counts.set(e.reason, (counts.get(e.reason) ?? 0) + 1)
    return counts
  }, [entries])

  const needle = query.trim().toLowerCase()
  const shown = entries.filter(
    (e) =>
      (!reason || e.reason === reason) &&
      (!needle || e.seriesTitle.toLowerCase().includes(needle) || e.fileName.toLowerCase().includes(needle)),
  )
  const groups = groupBySeries(shown)
  const filtered = reason !== null || needle !== ''
  const expiringSoon = entries.filter((e) => daysLeft(e.expiresAt) <= SOON_DAYS).length
  const total = entries.length

  const isOpen = (group: BinGroup) =>
    needle !== '' || (group.entries.length <= OPEN_UP_TO) !== toggled.has(group.seriesId)
  const toggle = (seriesId: number) =>
    setToggled((prev) => {
      const next = new Set(prev)
      if (next.has(seriesId)) next.delete(seriesId)
      else next.add(seriesId)
      return next
    })

  const restoreGroup = (group: BinGroup) =>
    restoreMany.mutate(
      group.entries.filter((e) => !e.missing).map((e) => e.id),
      { onSuccess: (r) => bulkToast(r, true) },
    )

  return (
    <Modal
      opened={opened}
      onClose={onClose}
      size="xl"
      title={
        <ModalHeading
          title={t`Recycle bin`}
          description={
            <Plural
              value={days}
              one="Deleted files wait here for # day, then they're gone for good."
              other="Deleted files wait here for # days, then they're gone for good."
            />
          }
        />
      }
    >
      {isLoading ? (
        <BinSkeleton />
      ) : isError ? (
        <EmptyState compact title={t`Couldn't load the recycle bin`} actionLabel={t`Retry`} onAction={() => void refetch()} />
      ) : total === 0 ? (
        <EmptyState
          compact
          mood="pleased"
          title={t`The recycle bin is empty`}
          description={t`Files you delete from the library or a series page land here first, so a slip can be undone.`}
        />
      ) : (
        <>
          <div className="recycle-bin-rail">
            <Tabs
              className="recycle-bin-tabs"
              value={reason ?? 'all'}
              onChange={(v) => setReason(!v || v === 'all' ? null : (v as RecycleReason))}
              variant="unstyled"
              classNames={{ list: 'series-tabs', tab: 'series-tab' }}
            >
              <Tabs.List>
                <Tabs.Tab value="all">
                  <Trans>All</Trans>
                </Tabs.Tab>
                {REASON_ORDER.filter((r) => reasonCounts.has(r)).map((r) => (
                  <Tabs.Tab key={r} value={r}>
                    {renderLabel(REASON_LABELS[r])}{' '}
                    <span className="recycle-bin-tab-count tnum">{reasonCounts.get(r)}</span>
                  </Tabs.Tab>
                ))}
              </Tabs.List>
            </Tabs>
            <SearchInput
              className="recycle-bin-search"
              placeholder={t`Filter by series or file`}
              aria-label={t`Filter by series or file`}
              value={query}
              onClear={() => setQuery('')}
              onChange={(e) => setQuery(e.currentTarget.value)}
            />
          </div>

          {groups.length === 0 ? (
            <EmptyState compact mood="asking" title={t`Nothing matches`} description={t`Try clearing the filters.`} />
          ) : (
            <div className="recycle-bin-list">
              {groups.map((group) => (
                <BinGroupSection
                  key={group.seriesId}
                  group={group}
                  coverUrl={covers.get(group.seriesId) ?? null}
                  opened={isOpen(group)}
                  onToggle={() => toggle(group.seriesId)}
                  restoring={restoreMany.isPending && group.entries.some((e) => restoreMany.variables?.includes(e.id))}
                  onRestoreAll={() => restoreGroup(group)}
                  onDeleteAll={() => setDeleting({ entries: group.entries, title: group.seriesTitle })}
                  onDelete={(entry) => setDeleting({ entries: [entry], title: group.seriesTitle })}
                />
              ))}
            </div>
          )}
        </>
      )}

      <ModalFooter
        status={
          total > 0 && (
            <MetaList
              inline
              items={[
                filtered ? (
                  <Plural
                    key="shown"
                    value={shown.length}
                    one={`Showing # of ${total} files`}
                    other={`Showing # of ${total} files`}
                  />
                ) : (
                  <Plural key="all" value={total} one={`# file, ${totalSize}`} other={`# files, ${totalSize}`} />
                ),
                expiringSoon > 0 && (
                  <span key="soon" className="recycle-bin-soon">
                    <Plural
                      value={expiringSoon}
                      one={`# goes within ${SOON_DAYS} days`}
                      other={`# go within ${SOON_DAYS} days`}
                    />
                  </span>
                ),
              ]}
            />
          )
        }
      >
        <Button
          variant="light"
          color="var(--danger)"
          leftSection={<IconTrash size={16} />}
          disabled={total === 0}
          onClick={() => setEmptying(true)}
        >
          <Trans>Empty bin</Trans>
        </Button>
      </ModalFooter>

      <ConfirmDialog
        opened={deleting !== null}
        onClose={() => setDeleting(null)}
        title={
          deleting && deleting.entries.length > 1 ? (
            <Plural
              value={deleting.entries.length}
              one="Delete # file permanently?"
              other="Delete # files permanently?"
            />
          ) : (
            <Trans>Delete this file permanently?</Trans>
          )
        }
        confirmLabel={<Trans>Delete permanently</Trans>}
        loading={removeMany.isPending}
        onConfirm={() =>
          deleting &&
          removeMany.mutate(
            deleting.entries.map((e) => e.id),
            {
              onSuccess: (r) => {
                setDeleting(null)
                if (r.failed > 0 || (deleting?.entries.length ?? 0) > 1) bulkToast(r, false)
              },
            },
          )
        }
      >
        <Stack gap="xs">
          {deleting?.entries.length === 1 ? (
            <Text size="sm" ff="monospace" style={{ wordBreak: 'break-all' }}>
              {deleting.entries[0].relativePath}
            </Text>
          ) : (
            <Text size="sm">
              <Trans>Every file from {deleting?.title} in the bin.</Trans>
            </Text>
          )}
          <Text size="sm" c="var(--danger)">
            <Trans>This action cannot be undone.</Trans>
          </Text>
        </Stack>
      </ConfirmDialog>

      <ConfirmDialog
        opened={emptying}
        onClose={() => setEmptying(false)}
        title={<Trans>Empty the recycle bin?</Trans>}
        confirmLabel={<Trans>Empty bin</Trans>}
        loading={empty.isPending}
        onConfirm={() =>
          empty.mutate(undefined, {
            onSuccess: (r) => {
              setEmptying(false)
              if (r.failed > 0) {
                notifications.show({
                  color: 'var(--danger)',
                  message: plural(r.failed, {
                    one: "Couldn't delete # file, check the log",
                    other: "Couldn't delete # files, check the log",
                  }),
                })
              }
            },
          })
        }
      >
        <Stack gap="xs">
          <Text size="sm">
            <Plural value={entries.length} one="Deletes # file for good." other="Deletes # files for good." />
          </Text>
          <Text size="sm" c="var(--danger)">
            <Trans>This action cannot be undone.</Trans>
          </Text>
        </Stack>
      </ConfirmDialog>
    </Modal>
  )
}

function BinGroupSection({
  group,
  coverUrl,
  opened,
  onToggle,
  restoring,
  onRestoreAll,
  onDeleteAll,
  onDelete,
}: {
  group: BinGroup
  coverUrl: string | null
  opened: boolean
  onToggle: () => void
  restoring: boolean | undefined
  onRestoreAll: () => void
  onDeleteAll: () => void
  onDelete: (entry: RecycleBinEntry) => void
}) {
  const { t } = useLingui()
  const bodyId = `recycle-bin-group-${group.seriesId}`
  const count = group.entries.length
  const size = formatBytes(group.size)
  const restorable = group.entries.some((e) => !e.missing)
  const soon = group.soonest <= SOON_DAYS

  return (
    <section className="recycle-bin-group" data-open={opened || undefined} aria-label={group.seriesTitle}>
      <div className="recycle-bin-group-head">
        <UnstyledButton
          className="recycle-bin-group-toggle"
          aria-expanded={opened}
          aria-controls={bodyId}
          onClick={onToggle}
        >
          <Thumb src={coverUrl} size="sm" aria-hidden />
          <span className="recycle-bin-group-text">
            <Text component="span" className="recycle-bin-group-title" fw={600} size="sm">
              {group.seriesTitle}
            </Text>
            <Text component="span" size="xs" c="var(--ink-3)">
              <MetaList
                inline
                items={[
                  plural(count, { one: `# file, ${size}`, other: `# files, ${size}` }),
                  !group.seriesExists && t`No longer in the library`,
                  <span key="soon" className={soon ? 'recycle-bin-soon' : undefined}>
                    {plural(group.soonest, { one: 'next goes in # day', other: 'next goes in # days' })}
                  </span>,
                ]}
              />
            </Text>
          </span>
          <IconChevronDown size={16} className="recycle-bin-chevron" aria-hidden />
        </UnstyledButton>
        {count > 1 && (
          <Group gap={4} wrap="nowrap" className="recycle-bin-group-actions">
            <Button
              size="xs"
              variant="default"
              leftSection={<IconRestore size={16} />}
              disabled={!restorable}
              loading={restoring}
              onClick={onRestoreAll}
            >
              <Trans>Restore all</Trans>
            </Button>
            <Tooltip label={t`Delete all permanently`} withArrow>
              <ActionIcon
                variant="subtle"
                color="var(--danger)"
                aria-label={t`Delete all permanently`}
                onClick={onDeleteAll}
              >
                <IconTrash size={16} />
              </ActionIcon>
            </Tooltip>
          </Group>
        )}
      </div>
      <Collapse expanded={opened} id={bodyId}>
        <div className="recycle-bin-rows">
          {group.entries.map((entry) => (
            <BinRow key={entry.id} entry={entry} onDelete={() => onDelete(entry)} />
          ))}
        </div>
      </Collapse>
    </section>
  )
}

function BinRow({ entry, onDelete }: { entry: RecycleBinEntry; onDelete: () => void }) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const restore = useRestoreFromRecycleBin()
  const left = daysLeft(entry.expiresAt)
  const deletedBy = entry.deletedBy

  const onRestore = () =>
    restore.mutate(entry.id, {
      onSuccess: (r) =>
        notifications.show({
          color: 'var(--ok)',
          message: r.linked
            ? plural(r.chapters, {
                one: 'Restored and linked to # chapter',
                other: 'Restored and linked to # chapters',
              })
            : now`Restored as an unlinked file. Health can import it.`,
        }),
    })

  return (
    <div className="recycle-bin-row" data-missing={entry.missing || undefined}>
      <div className="recycle-bin-row-main">
        <Text size="sm" className="recycle-bin-file">
          {entry.fileName}
        </Text>
        <Text component="div" size="xs" c="var(--ink-3)">
          <MetaList
            inline
            items={[
              renderLabel(REASON_LABELS[entry.reason]),
              chapterLabel(entry),
              deletedBy ? t`${formatDate(entry.deletedAt)} by ${deletedBy}` : formatDate(entry.deletedAt),
            ]}
          />
        </Text>
        {entry.missing && (
          <Text size="xs" c="var(--danger)">
            <Trans>The file is missing from the bin folder.</Trans>
          </Text>
        )}
      </div>
      <Text size="sm" className="recycle-bin-row-size tnum">
        {formatBytes(entry.size)}
      </Text>
      <Text size="sm" className="recycle-bin-row-left tnum" data-soon={left <= SOON_DAYS || undefined}>
        <Plural value={left} one="# day left" other="# days left" />
      </Text>
      <Group gap={4} wrap="nowrap" justify="flex-end" className="recycle-bin-row-actions">
        <Button
          size="xs"
          variant="default"
          leftSection={<IconRestore size={16} />}
          disabled={entry.missing}
          loading={restore.isPending}
          onClick={onRestore}
        >
          <Trans>Restore</Trans>
        </Button>
        <Tooltip label={t`Delete permanently`} withArrow>
          <ActionIcon
            variant="subtle"
            color="var(--danger)"
            onClick={onDelete}
            aria-label={t`Delete permanently`}
          >
            <IconTrash size={16} />
          </ActionIcon>
        </Tooltip>
      </Group>
    </div>
  )
}

function BinSkeleton() {
  return (
    <div className="recycle-bin-list" aria-hidden>
      {[60, 44, 52].map((width, i) => (
        <Group key={i} className="recycle-bin-group-head" gap="sm" wrap="nowrap">
          <Skeleton w={28} h={40} radius="xs" />
          <Stack gap={6} style={{ flex: 1 }}>
            <Skeleton h={12} w={width * 4} maw="70%" />
            <Skeleton h={10} w={(width - 16) * 4} maw="50%" />
          </Stack>
        </Group>
      ))}
    </div>
  )
}
