import { useMemo, useState } from 'react'
import {
  Badge,
  Button,
  Checkbox,
  Group,
  Image,
  Pagination,
  Select,
  Stack,
  Switch,
  Table,
  Text,
  Tooltip,
} from '@mantine/core'
import { IconPackageImport, IconSearch } from '@tabler/icons-react'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { useLibrarySettings, useRootFolders } from '../../api/hooks'
import { useAuth } from '../../auth/AuthProvider'
import { useLabel } from '../../i18n-context'
import type { MihonParseResponse } from '../../api/mihonImport'
import type { MetadataSearchResult } from '../../api/types'
import { Panel } from '../ui/Panel'
import { TagChip } from '../ui/TagChip'
import { okButtonVars } from '../ui/status'
import { MONITOR_OPTIONS } from '../series/SeriesActionsMenu'
import { ImportMatchFinder } from './ImportMatchFinder'
import {
  MATCHED_BY_LABELS,
  buildGroups,
  effectiveMatch,
  isChecked,
  isSelectable,
  rowFilter,
  type MihonFilter,
  type MihonGroup,
  type MihonRow,
  type MihonRowChoice,
} from './mihonRows'

const PAGE_SIZE = 100

export interface MihonImportOptions {
  rootFolderId: number
  monitored: boolean
  monitorNewItems: string | null
  applyTags: boolean
  applyReadState: boolean
}

export function MihonReview({
  parsed,
  rows,
  onStart,
}: {
  parsed: MihonParseResponse
  rows: MihonRow[]
  onStart: (groups: MihonGroup[], options: MihonImportOptions) => void
}) {
  const { t } = useLingui()
  const label = useLabel()
  const { can } = useAuth()
  const canTag = can('ManageTags')
  const { data: rootFolders } = useRootFolders()
  const [rootFolderId, setRootFolderId] = useState<string | null>(null)
  const [monitored, setMonitored] = useState(true)
  const { data: librarySettings } = useLibrarySettings()
  const [monitorNewItems, setMonitorNewItems] = useState<string | null>(null)
  const [applyTags, setApplyTags] = useState(true)
  const [applyReadState, setApplyReadState] = useState(true)
  const [choices, setChoices] = useState<Record<number, MihonRowChoice>>({})
  const [filter, setFilter] = useState<MihonFilter>('all')
  const [page, setPage] = useState(1)
  const [finderFor, setFinderFor] = useState<MihonRow | null>(null)

  const anyCategories = rows.some((r) => r.manga.categories.length > 0)
  const effectiveRoot = rootFolderId ?? (rootFolders?.[0] ? String(rootFolders[0].id) : null)

  const counts = useMemo(() => {
    const c: Record<MihonFilter, number> = { all: rows.length, matched: 0, needsReview: 0, notFound: 0, inLibrary: 0 }
    for (const row of rows) c[rowFilter(row, choices[row.manga.index])]++
    return c
  }, [rows, choices])

  const filtered = useMemo(
    () => (filter === 'all' ? rows : rows.filter((r) => rowFilter(r, choices[r.manga.index]) === filter)),
    [rows, choices, filter],
  )
  const pageCount = Math.max(1, Math.ceil(filtered.length / PAGE_SIZE))
  const safePage = Math.min(page, pageCount)
  const pageRows = filtered.slice((safePage - 1) * PAGE_SIZE, safePage * PAGE_SIZE)

  const selectable = filtered.filter((r) => isSelectable(r, choices[r.manga.index]))
  const checkedInFilter = selectable.filter((r) => isChecked(r, choices[r.manga.index])).length
  const groups = useMemo(() => buildGroups(rows, choices), [rows, choices])

  const patch = (index: number, change: Partial<MihonRowChoice>) =>
    setChoices((c) => ({ ...c, [index]: { ...c[index], ...change } }))

  const selectAllInFilter = (checked: boolean) =>
    setChoices((c) => {
      const next = { ...c }
      for (const row of selectable) next[row.manga.index] = { ...next[row.manga.index], checked }
      return next
    })

  const pickManual = (row: MihonRow, match: MetadataSearchResult) => {
    patch(row.manga.index, { manual: match, checked: true })
    setFinderFor(null)
  }

  const chips: { key: MihonFilter; text: string }[] = [
    { key: 'all', text: t`All` },
    { key: 'matched', text: t`Matched` },
    { key: 'needsReview', text: t`Needs review` },
    { key: 'notFound', text: t`Not found` },
    { key: 'inLibrary', text: t`In library` },
  ]

  const start = () => {
    if (!effectiveRoot || groups.length === 0) return
    onStart(groups, {
      rootFolderId: Number(effectiveRoot),
      monitored,
      monitorNewItems,
      applyTags: applyTags && canTag,
      applyReadState,
    })
  }

  const skippedNonLibrary = parsed.skippedNonLibrary

  return (
    <Stack gap="md">
      <Text size="sm" c="var(--ink-2)">
        <Plural value={rows.length} one="# manga in your Mihon library." other="# manga in your Mihon library." />{' '}
        {skippedNonLibrary > 0 && (
          <Plural
            value={skippedNonLibrary}
            one="# manga that is not in the library was skipped."
            other="# manga that are not in the library were skipped."
          />
        )}
      </Text>

      <Panel>
        <Stack gap="sm">
          <Select
            label={t`Root folder`}
            data={rootFolders?.map((f) => ({ value: String(f.id), label: f.path })) ?? []}
            value={effectiveRoot}
            onChange={setRootFolderId}
            allowDeselect={false}
          />
          <Group gap="xl" align="flex-start">
            <Switch
              label={t`Monitor for new chapters`}
              checked={monitored}
              onChange={(e) => setMonitored(e.currentTarget.checked)}
            />
            <Select
              label={t`New chapters`}
              data={MONITOR_OPTIONS.map((o) => ({ value: o.value, label: label(o.label) }))}
              value={monitorNewItems ?? librarySettings?.newSeriesMonitorMode ?? 'All'}
              onChange={(v) => v && setMonitorNewItems(v)}
              allowDeselect={false}
              size="xs"
              w={220}
            />
          </Group>
          {canTag && (
            <Switch
              label={t`Apply categories as tags`}
              description={
                anyCategories
                  ? t`Each Mihon category becomes a tag on the series. Manga in Mihon's Default category get no tag.`
                  : t`This backup has no categories.`
              }
              checked={applyTags}
              disabled={!anyCategories}
              onChange={(e) => setApplyTags(e.currentTarget.checked)}
            />
          )}
          <Switch
            label={t`Mark chapters read in Mihon as read`}
            description={t`Read ticks show in the chapter list. Series read counts only include downloaded chapters.`}
            checked={applyReadState}
            onChange={(e) => setApplyReadState(e.currentTarget.checked)}
          />
        </Stack>
      </Panel>

      <Group gap="xs" role="group" aria-label={t`Filter by match status`}>
        {chips.map((chip) => (
          <TagChip
            key={chip.key}
            active={filter === chip.key}
            onClick={() => {
              setFilter(chip.key)
              setPage(1)
            }}
          >
            {chip.text} ({counts[chip.key]})
          </TagChip>
        ))}
      </Group>

      <Group justify="space-between" align="center">
        <Checkbox
          label={<Plural value={selectable.length} one="Select all # in this view" other="Select all # in this view" />}
          checked={selectable.length > 0 && checkedInFilter === selectable.length}
          indeterminate={checkedInFilter > 0 && checkedInFilter < selectable.length}
          disabled={selectable.length === 0}
          onChange={(e) => selectAllInFilter(e.currentTarget.checked)}
        />
        <Button
          color="var(--ok)"
          vars={okButtonVars}
          leftSection={<IconPackageImport size={16} />}
          disabled={groups.length === 0 || !effectiveRoot}
          onClick={start}
        >
          <Plural value={groups.length} one="Import # series" other="Import # series" />
        </Button>
      </Group>
      {counts.inLibrary > 0 && (
        <Text size="xs" c="var(--ink-3)">
          <Trans>
            Series already in your library are not added again. Importing them only applies tags and read state.
          </Trans>
        </Text>
      )}

      {filtered.length === 0 ? (
        <Text size="sm" c="var(--ink-3)">
          <Trans>Nothing in this view.</Trans>
        </Text>
      ) : (
        <Panel p={0} className="table-panel">
          <Table.ScrollContainer minWidth={760}>
            <Table className="panel-table import-table">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th w={40} />
                  <Table.Th>
                    <Trans>Mihon title</Trans>
                  </Table.Th>
                  <Table.Th>
                    <Trans>Match</Trans>
                  </Table.Th>
                  <Table.Th w={140} />
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {pageRows.map((row) => {
                  const choice = choices[row.manga.index]
                  const { match, matchedBy, library } = effectiveMatch(row, choice)
                  const selectableRow = isSelectable(row, choice)
                  const { title, categories, readCount } = row.manga
                  return (
                    <Table.Tr key={row.manga.index}>
                      <Table.Td>
                        <Tooltip
                          label={t`This series is already in the library, but you can't see it, so it can't be changed here.`}
                          disabled={library !== 'elsewhere'}
                          withArrow
                          multiline
                          w={240}
                        >
                          <span>
                            <Checkbox
                              aria-label={t`Import ${title}`}
                              checked={isChecked(row, choice)}
                              disabled={!selectableRow}
                              onChange={(e) => patch(row.manga.index, { checked: e.currentTarget.checked })}
                            />
                          </span>
                        </Tooltip>
                      </Table.Td>
                      <Table.Td>
                        <Text size="sm" fw={600}>
                          {title}
                        </Text>
                        <Group gap={4} mt={2}>
                          {categories.map((c) => (
                            <TagChip key={c} size="sm">
                              {c}
                            </TagChip>
                          ))}
                          {readCount > 0 && (
                            <Text size="xs" c="var(--ink-3)">
                              <Plural value={readCount} one="# chapter read" other="# chapters read" />
                            </Text>
                          )}
                        </Group>
                      </Table.Td>
                      <Table.Td>
                        {row.result.status === 'needsReview' && !choice?.manual ? (
                          <Select
                            aria-label={t`Match for ${title}`}
                            placeholder={t`Choose a match`}
                            data={row.result.candidates.map((c) => ({
                              value: c.providerId,
                              label: `${c.title}${c.year ? ` (${c.year})` : ''}`,
                            }))}
                            value={choice?.picked ?? null}
                            onChange={(v) => patch(row.manga.index, { picked: v ?? undefined, checked: v !== null })}
                            leftSection={
                              match?.coverUrl ? (
                                <Image src={match.coverUrl} w={20} h={30} radius="xs" fit="cover" alt="" />
                              ) : undefined
                            }
                            leftSectionWidth={match?.coverUrl ? 36 : undefined}
                          />
                        ) : match ? (
                          <Group wrap="nowrap" gap="xs">
                            {match.coverUrl ? (
                              <Image src={match.coverUrl} w={32} h={48} radius="sm" fit="cover" alt="" loading="lazy" />
                            ) : (
                              <div style={{ width: 32, height: 48 }} />
                            )}
                            <div style={{ minWidth: 0 }}>
                              <Text size="sm" lineClamp={1}>
                                {match.title}
                                {match.year ? ` (${match.year})` : ''}
                              </Text>
                              <Group gap={4}>
                                {matchedBy && !choice?.manual && (
                                  <Badge size="xs" variant="light">
                                    {label(MATCHED_BY_LABELS[matchedBy])}
                                  </Badge>
                                )}
                                {library !== 'none' && (
                                  <Badge
                                    size="xs"
                                    variant="light"
                                    color={library === 'elsewhere' ? 'var(--warn)' : undefined}
                                  >
                                    <Trans>In library</Trans>
                                  </Badge>
                                )}
                              </Group>
                            </div>
                          </Group>
                        ) : (
                          <Text size="sm" c="var(--danger)">
                            <Trans>No match found.</Trans>
                          </Text>
                        )}
                      </Table.Td>
                      <Table.Td>
                        <Button
                          size="xs"
                          variant="light"
                          leftSection={<IconSearch size={14} />}
                          onClick={() => setFinderFor(row)}
                        >
                          {match ? <Trans>Change</Trans> : <Trans>Find match</Trans>}
                        </Button>
                      </Table.Td>
                    </Table.Tr>
                  )
                })}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        </Panel>
      )}

      {pageCount > 1 && <Pagination total={pageCount} value={safePage} onChange={setPage} />}

      {finderFor && (
        <ImportMatchFinder
          folderName={finderFor.manga.title}
          initialQuery={finderFor.manga.title}
          onPick={(m) => pickManual(finderFor, m)}
          onClose={() => setFinderFor(null)}
        />
      )}
    </Stack>
  )
}
