import { useEffect, useRef, useState } from 'react'
import { Alert, Button, FileInput, Progress, Stack, Text } from '@mantine/core'
import { IconFileZip } from '@tabler/icons-react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
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
  | { kind: 'review' }
  | { kind: 'importing'; done: number; total: number }
  | { kind: 'done' }

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
    setPhase({ kind: 'pick' })
  }

  const resolveAll = async (data: MihonParseResponse) => {
    const token = ++runRef.current
    const results = new Map<number, MihonResolveResult>()
    setPhase({ kind: 'resolving', done: 0, total: data.manga.length })
    try {
      for (let i = 0; i < data.manga.length; i += IMPORT_BATCH_SIZE) {
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
        setPhase({ kind: 'resolving', done: Math.min(i + batch.length, data.manga.length), total: data.manga.length })
      }
    } catch (e) {
      if (token !== runRef.current) return
      setError(errorText(e))
      setPhase({ kind: 'pick' })
      return
    }
    setRows(
      data.manga.flatMap((manga) => {
        const result = results.get(manga.index)
        return result ? [{ manga, result }] : []
      }),
    )
    setPhase({ kind: 'review' })
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
    onError: (e) => setError(errorText(e)),
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
          <Button disabled={!file} loading={parse.isPending} onClick={() => file && parse.mutate(file)}>
            <Trans>Read backup</Trans>
          </Button>
        </div>
      </Stack>
    </Panel>
  )
}
