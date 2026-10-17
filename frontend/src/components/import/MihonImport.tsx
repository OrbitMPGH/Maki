import { useEffect, useRef, useState } from 'react'
import { Alert, Button, FileInput, Group, Progress, Stack, Text } from '@mantine/core'
import { IconFileZip } from '@tabler/icons-react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { ApiError } from '../../api/client'
import { errorText } from '../../api/errorText'
import { IMPORT_BATCH_SIZE } from '../../api/libraryImport'
import {
  importMihon,
  parseMihonBackup,
  resolveMihon,
  type MihonImportResult,
  type MihonParseResponse,
  type MihonResolveResult,
} from '../../api/mihonImport'
import { randomUUID } from '../../lib/uuid'
import { Panel } from '../ui/Panel'
import { MihonReview, type MihonImportOptions } from './MihonReview'
import { MihonSummary, type MihonOutcomeRow } from './MihonSummary'
import type { MihonGroup, MihonRow } from './mihonRows'

type Phase =
  | { kind: 'pick' }
  | { kind: 'resolving'; done: number; total: number }
  | { kind: 'resolveFailed'; done: number; total: number; message: string }
  | { kind: 'review' }
  | { kind: 'importing'; done: number; total: number }
  | { kind: 'done' }

const MAX_BACKUP_MB = 100
const MAX_BACKUP_BYTES = MAX_BACKUP_MB * 1024 * 1024

/**
 * Importing a Mihon backup: upload, match every library manga to MangaBaka in batches, review, then
 * create the series in batches. The server is stateless, so each step is driven from here.
 */
export function MihonImport() {
  const { t } = useLingui()
  const queryClient = useQueryClient()
  const [file, setFile] = useState<File | null>(null)
  const [phase, setPhase] = useState<Phase>({ kind: 'pick' })
  const [parsed, setParsed] = useState<MihonParseResponse | null>(null)
  const [rows, setRows] = useState<MihonRow[]>([])
  const [error, setError] = useState<string | null>(null)
  const [outcomes, setOutcomes] = useState<MihonOutcomeRow[]>([])
  // What a failed resolve batch leaves behind: finished results and the offset to resume from.
  const partialRef = useRef<{ data: MihonParseResponse; results: Map<number, MihonResolveResult>; offset: number } | null>(
    null,
  )
  // Bumped to abandon a resolve loop that is still awaiting a batch (cancel, restart or unmount).
  const runRef = useRef(0)

  useEffect(
    () => () => {
      runRef.current++
    },
    [],
  )

  const reset = () => {
    setFile(null)
    setParsed(null)
    setRows([])
    setOutcomes([])
    setError(null)
    partialRef.current = null
    setPhase({ kind: 'pick' })
  }

  const sizeMessage = () =>
    t`That backup is larger than the ${MAX_BACKUP_MB} MB limit, so it can't be imported.`

  const finishResolve = (data: MihonParseResponse, results: Map<number, MihonResolveResult>) => {
    partialRef.current = null
    setRows(
      data.manga.map((manga) => ({
        manga,
        result: results.get(manga.index) ?? {
          index: manga.index,
          status: 'notFound',
          matchedBy: null,
          match: null,
          candidates: [],
          library: 'none',
          seriesId: null,
        },
      })),
    )
    setPhase({ kind: 'review' })
  }

  const resolveAll = async (
    data: MihonParseResponse,
    from = 0,
    results = new Map<number, MihonResolveResult>(),
  ) => {
    const token = ++runRef.current
    const total = data.manga.length
    setPhase({ kind: 'resolving', done: from, total })
    let i = from
    try {
      for (; i < data.manga.length; i += IMPORT_BATCH_SIZE) {
        const batch = data.manga.slice(i, i + IMPORT_BATCH_SIZE)
        const res = await resolveMihon(
          batch.map(({ index, title, author, aniListId, malId, kitsuId, mangaBakaId }) => ({
            index,
            title,
            author,
            aniListId,
            malId,
            kitsuId,
            mangaBakaId,
          })),
        )
        if (token !== runRef.current) return
        for (const r of res) results.set(r.index, r)
        setPhase({ kind: 'resolving', done: Math.min(i + batch.length, total), total })
      }
    } catch (e) {
      if (token !== runRef.current) return
      partialRef.current = { data, results, offset: i }
      setPhase({ kind: 'resolveFailed', done: i, total, message: errorText(e) })
      return
    }
    finishResolve(data, results)
  }

  const retryResolve = () => {
    const partial = partialRef.current
    if (partial) void resolveAll(partial.data, partial.offset, partial.results)
  }

  const continueWithPartial = () => {
    const partial = partialRef.current
    if (partial) finishResolve(partial.data, partial.results)
  }

  const startParse = () => {
    if (!file) return
    if (file.size > MAX_BACKUP_BYTES) {
      setError(sizeMessage())
      return
    }
    parse.mutate(file)
  }

  const parse = useMutation({
    mutationFn: parseMihonBackup,
    meta: { silent: true },
    onSuccess: (data) => {
      setParsed(data)
      setError(null)
      if (data.manga.length === 0) {
        setPhase({ kind: 'pick' })
        return
      }
      void resolveAll(data)
    },
    onError: (e) => setError(e instanceof ApiError && e.status === 413 ? sizeMessage() : errorText(e)),
  })

  const cancelResolve = () => {
    runRef.current++
    reset()
  }

  const runImport = async (groups: MihonGroup[], options: MihonImportOptions) => {
    const runId = randomUUID()
    const { applyTags, applyReadState } = options
    const collected: MihonOutcomeRow[] = []
    const titles = new Map(groups.map((g) => [g.providerId, g.title]))
    setOutcomes([])
    setPhase({ kind: 'importing', done: 0, total: groups.length })
    for (let i = 0; i < groups.length; i += IMPORT_BATCH_SIZE) {
      const batch = groups.slice(i, i + IMPORT_BATCH_SIZE)
      let results: MihonImportResult[]
      try {
        results = await importMihon({
          runId,
          ...options,
          items: batch.map((g) => ({
            providerId: g.providerId,
            categories: applyTags ? g.categories : [],
            readNumbers: applyReadState ? g.readNumbers : [],
            trackerLastRead: applyReadState ? g.trackerLastRead : null,
          })),
        })
      } catch (e) {
        // A batch the server never answered: report its rows as failed and carry on with the rest.
        const reason = errorText(e)
        results = batch.map((g) => ({
          providerId: g.providerId,
          outcome: 'failed' as const,
          seriesId: null,
          reason,
        }))
      }
      for (const r of results) collected.push({ ...r, title: titles.get(r.providerId) ?? r.providerId })
      setOutcomes([...collected])
      setPhase({ kind: 'importing', done: Math.min(i + batch.length, groups.length), total: groups.length })
    }
    setPhase({ kind: 'done' })
    void Promise.all(
      [['series'], ['tags'], ['chapters']].map((queryKey) => queryClient.invalidateQueries({ queryKey })),
    )
  }

  if (phase.kind === 'review' && parsed) {
    return <MihonReview parsed={parsed} rows={rows} onStart={(g, o) => void runImport(g, o)} />
  }

  if (phase.kind === 'done') {
    return <MihonSummary results={outcomes} onReset={reset} />
  }

  if (phase.kind === 'resolveFailed') {
    const { done, total, message } = phase
    return (
      <Panel>
        <Stack gap="sm">
          <Alert color="var(--danger)" variant="light">
            {message}
          </Alert>
          <Text size="sm" c="var(--ink-2)">
            <Trans>
              {done} of {total} matched before this stopped. Retry to carry on, or review what matched so far. The
              rest will show as not found, and you can match them by hand.
            </Trans>
          </Text>
          <Group>
            <Button onClick={retryResolve}>
              <Trans>Retry</Trans>
            </Button>
            <Button variant="default" onClick={continueWithPartial}>
              <Trans>Continue to review with what resolved</Trans>
            </Button>
            <Button variant="subtle" onClick={cancelResolve}>
              <Trans>Cancel</Trans>
            </Button>
          </Group>
        </Stack>
      </Panel>
    )
  }

  if (phase.kind === 'resolving' || phase.kind === 'importing') {
    const { done, total } = phase
    return (
      <Panel>
        <Stack gap="sm">
          <Text fw={600}>
            {phase.kind === 'resolving' ? <Trans>Matching your library to MangaBaka</Trans> : <Trans>Importing</Trans>}
          </Text>
          <Progress value={total === 0 ? 0 : (100 * done) / total} animated />
          <Text size="sm" c="var(--ink-3)">
            <Trans>
              {done} of {total}
            </Trans>
          </Text>
          {phase.kind === 'resolving' ? (
            <div>
              <Button variant="default" onClick={cancelResolve}>
                <Trans>Cancel</Trans>
              </Button>
            </div>
          ) : (
            <Text size="xs" c="var(--ink-3)">
              <Trans>Keep this tab open until it finishes.</Trans>
            </Text>
          )}
        </Stack>
      </Panel>
    )
  }

  return (
    <Panel>
      <Stack gap="sm">
        <FileInput
          label={t`Mihon backup`}
          description={t`In Mihon, open More > Settings > Data and storage > Create backup, then choose the .tachibk file it makes. Only manga in your library are imported.`}
          placeholder={t`Choose a .tachibk file`}
          accept=".tachibk,.proto.gz,.gz"
          leftSection={<IconFileZip size={16} />}
          value={file}
          onChange={(f) => {
            setFile(f)
            setError(null)
            setParsed(null)
          }}
          clearable
        />
        {error && (
          <Alert color="var(--danger)" variant="light">
            {error}
          </Alert>
        )}
        {parsed && parsed.manga.length === 0 && (
          <Text size="sm" c="var(--ink-2)">
            <Plural
              value={parsed.skippedNonLibrary}
              one="This backup has no manga in the library. # manga that is not in the library was skipped."
              other="This backup has no manga in the library. # manga that are not in the library were skipped."
            />
          </Text>
        )}
        <div>
          <Button disabled={!file} loading={parse.isPending} onClick={startParse}>
            <Trans>Read backup</Trans>
          </Button>
        </div>
      </Stack>
    </Panel>
  )
}
