import { errorText } from '../api/errorText'
import { Badge, Button, Group, Modal, Stack, Table, Text, TextInput, Tooltip, VisuallyHidden } from '@mantine/core'
import { notifications } from '../lib/toast'
import { useEffect, useRef, useState } from 'react'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { i18n } from '@lingui/core'
import { t as now } from '@lingui/core/macro'
import { useGrabRelease, useReleaseSearch } from '../api/hooks'
import { ApiError } from '../api/client'
import {
  QUALITY_TIER_COLOR,
  QUALITY_TIER_LABELS,
  SPAN_VERDICT_COLOR,
  SPAN_VERDICT_LABELS,
  proposalCountsText,
  proposalReasonLabel,
  releaseSpanText,
  spanVerdictKey,
} from '../api/upgrades'
import { useLabel } from '../i18n-context'
import { formatBytes } from '../format'
import { IconArrowUp, IconSearch } from '../lib/icons'
import { EmptyState } from './ui/EmptyState'
import { ModalFooter, ModalHeading, ModalNote } from './ui/ModalShell'
import { Panel } from './ui/Panel'
import { TableSkeleton } from './ui/TableSkeleton'

const INDEXER_SETTINGS = '/settings?tab=downloads&s=indexer'
const WIDE = 'min(1180px, calc(100vw - 3rem))'

function seedHealth(seeders: number | null, belowMinimum: boolean): 'ok' | 'warn' | 'danger' {
  if (!seeders) return 'danger'
  return belowMinimum || seeders < 5 ? 'warn' : 'ok'
}

function listText(parts: string[]): string {
  return typeof Intl.ListFormat === 'function'
    ? new Intl.ListFormat(i18n.locale || undefined).format(parts)
    : parts.join(', ')
}

export function ReleaseSearchModal({
  seriesId,
  opened,
  onClose,
  titles = [],
}: {
  seriesId: number
  opened: boolean
  onClose: () => void
  /** The series title first, then alt titles; each gets a button that puts it in the search box. */
  titles?: string[]
}) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const [input, setInput] = useState('')
  const [manualQuery, setManualQuery] = useState<string | undefined>(undefined)
  const inputRef = useRef<HTMLInputElement>(null)
  const { data, isFetching, error } = useReleaseSearch(seriesId, opened, manualQuery)
  const releases = data?.releases
  const grab = useGrabRelease()
  const noIndexer = error instanceof ApiError && error.code === 'error.release.indexerNotConfigured'
  const resultCount = releases?.length ?? 0
  const indexerCount = new Set(releases?.map((r) => r.indexer)).size
  const tried = data?.tried ?? []
  const triedList = listText(tried)
  const minSeeders = data?.minSeeders ?? 1

  // Start each open with an automatic search; mirror whatever query was actually used
  // (the backend may have loosened it) into the input so the user can tweak it.
  useEffect(() => {
    if (opened) {
      setManualQuery(undefined)
      setInput('')
    }
  }, [opened, seriesId])
  useEffect(() => {
    if (data) {
      setInput(data.query)
    }
  }, [data])

  const search = () => {
    const q = input.trim()
    if (q) {
      setManualQuery(q)
    }
  }

  // Sized to what it holds: a results table needs the width, a one-line answer doesn't.
  const narrow = !isFetching && (error != null || (releases != null && releases.length === 0))

  return (
    <Modal
      opened={opened}
      onClose={onClose}
      size={narrow ? 560 : WIDE}
      title={
        <ModalHeading
          title={<Trans>Search releases</Trans>}
          description={<Trans>Searches your indexers. Grab sends a release to your download client.</Trans>}
        />
      }
    >
      {noIndexer ? (
        <EmptyState
          compact
          mood="asking"
          title={t`No indexer set up`}
          description={<Trans>Maki searches releases through Prowlarr or Jackett.</Trans>}
          actionLabel={t`Set up an indexer`}
          actionTo={INDEXER_SETTINGS}
        />
      ) : (
        <Stack gap="md">
          <Group gap="xs" wrap="nowrap">
            <TextInput
              ref={inputRef}
              style={{ flex: 1 }}
              aria-label={t`Search query`}
              placeholder={t`Search query`}
              leftSection={<IconSearch size={16} />}
              value={input}
              onChange={(e) => setInput(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter') {
                  search()
                }
              }}
              disabled={isFetching}
            />
            <Button variant="default" onClick={search} disabled={isFetching || !input.trim()}>
              <Trans>Search</Trans>
            </Button>
          </Group>

          {titles.length > 1 && (
            <Group gap={6} mt={-6}>
              <Text size="xs" c="var(--ink-3)">
                <Trans>Use title:</Trans>
              </Text>
              {titles.map((title) => (
                <Button
                  key={title}
                  size="compact-xs"
                  variant={input.trim() === title ? 'light' : 'default'}
                  disabled={isFetching}
                  onClick={() => {
                    setInput(title)
                    inputRef.current?.focus()
                  }}
                >
                  {title}
                </Button>
              ))}
            </Group>
          )}

          {isFetching && <TableSkeleton columns={6} rows={5} />}

          {error && !isFetching && <ModalNote>{errorText(error)}</ModalNote>}

          {releases && releases.length === 0 && !isFetching && (
            <EmptyState
              compact
              mood="asleep"
              title={t`No releases found`}
              description={
                tried.length > 1 ? (
                  <Trans>Searched for {triedList}. Try a shorter or alternative query.</Trans>
                ) : (
                  <Trans>Try a shorter or alternative query.</Trans>
                )
              }
            />
          )}

          {releases && releases.length > 0 && !isFetching && (
            <Panel p={0} className="table-panel">
              <Table.ScrollContainer minWidth={720}>
                <Table className="panel-table ops-table">
                  <Table.Thead>
                    <Table.Tr>
                      <Table.Th><Trans>Title</Trans></Table.Th>
                      <Table.Th><Trans>Indexer</Trans></Table.Th>
                      <Table.Th><Trans>Size</Trans></Table.Th>
                      <Table.Th><Trans>Seeds</Trans></Table.Th>
                      <Table.Th><Trans>Upgrade</Trans></Table.Th>
                      <Table.Th><VisuallyHidden><Trans>Actions</Trans></VisuallyHidden></Table.Th>
                    </Table.Tr>
                  </Table.Thead>
                  <Table.Tbody>
                    {releases.map((r) => {
                      const { parsed, ...release } = r
                      const { title, indexer, infoUrl, size, seeders } = release
                      const spanText = parsed ? releaseSpanText(parsed.span) : null
                      const verdictKey = parsed ? spanVerdictKey(parsed) : null
                      const relatedTitle = parsed?.rivalTitle
                      const belowMinimum = seeders != null && seeders < minSeeders
                      return (
                        <Table.Tr key={r.guid}>
                          <Table.Td>
                            <Text size="sm" style={{ wordBreak: 'break-word' }}>
                              {infoUrl ? (
                                <a href={infoUrl} target="_blank" rel="noreferrer">
                                  {title}
                                </a>
                              ) : (
                                title
                              )}
                            </Text>
                            {spanText && (
                              <Text size="xs" c="var(--ink-3)">
                                {spanText}
                              </Text>
                            )}
                          </Table.Td>
                          <Table.Td>
                            <Text size="sm" c="var(--ink-2)">
                              {indexer}
                            </Text>
                          </Table.Td>
                          <Table.Td>
                            <Text size="sm" className="tnum">
                              {formatBytes(size)}
                            </Text>
                          </Table.Td>
                          <Table.Td>
                            <Group gap={6} wrap="nowrap">
                              <Text
                                size="sm"
                                component="span"
                                className="release-search-seeds tnum"
                                data-health={seedHealth(seeders, belowMinimum)}
                              >
                                <IconArrowUp size={16} aria-hidden />
                                {seeders ?? '?'}
                              </Text>
                              {belowMinimum && (
                                <Tooltip
                                  withArrow
                                  multiline
                                  maw={260}
                                  label={
                                    <Trans>
                                      Fewer than {minSeeders} seeders, so Maki won't grab it on its own. You can still
                                      grab it here.
                                    </Trans>
                                  }
                                >
                                  <Badge size="sm" variant="light" color="var(--warn)" tabIndex={0}>
                                    <Trans>Under minimum</Trans>
                                  </Badge>
                                </Tooltip>
                              )}
                            </Group>
                          </Table.Td>
                          <Table.Td>
                            {parsed && verdictKey && (
                              <Tooltip
                                withArrow
                                multiline
                                maw={320}
                                label={
                                  <Stack gap={2}>
                                    <Text size="xs">{proposalCountsText(parsed)}</Text>
                                    {parsed.reasons.map((code) => (
                                      <Text key={code} size="xs">
                                        {proposalReasonLabel(renderLabel, code)}
                                      </Text>
                                    ))}
                                    {relatedTitle && (
                                      <Text size="xs" c="var(--ink-3)">
                                        {t`Related series: ${relatedTitle}`}
                                      </Text>
                                    )}
                                  </Stack>
                                }
                              >
                                <Group gap={4} wrap="nowrap">
                                  <Badge size="sm" variant="light" color={QUALITY_TIER_COLOR[parsed.tier]}>
                                    {renderLabel(QUALITY_TIER_LABELS[parsed.tier])}
                                  </Badge>
                                  <Badge size="sm" variant="light" color={SPAN_VERDICT_COLOR[verdictKey]}>
                                    {renderLabel(SPAN_VERDICT_LABELS[verdictKey])}
                                  </Badge>
                                </Group>
                              </Tooltip>
                            )}
                          </Table.Td>
                          <Table.Td ta="right">
                            <Button
                              size="compact-sm"
                              variant="subtle"
                              loading={grab.isPending && grab.variables?.release.guid === r.guid}
                              disabled={grab.isPending}
                              onClick={() =>
                                grab.mutate(
                                  { seriesId, release },
                                  {
                                    onSuccess: () => {
                                      notifications.show({
                                        message: now`Sent to the download client: ${title}`,
                                        color: 'var(--ok)',
                                      })
                                      onClose()
                                    },
                                  },
                                )
                              }
                            >
                              <Trans>Grab</Trans>
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
        </Stack>
      )}

      <ModalFooter
        status={
          isFetching ? (
            <Trans>Searching indexers…</Trans>
          ) : resultCount > 0 ? (
            <Trans>
              <Plural value={resultCount} one="# result" other="# results" /> from{' '}
              <Plural value={indexerCount} one="# indexer" other="# indexers" />
            </Trans>
          ) : null
        }
      >
        <Button variant="default" onClick={onClose}>
          <Trans>Close</Trans>
        </Button>
      </ModalFooter>
    </Modal>
  )
}
