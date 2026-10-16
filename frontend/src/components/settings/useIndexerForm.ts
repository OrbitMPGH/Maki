import { useEffect, useReducer, useState } from 'react'
import { notifications } from '@mantine/notifications'
import { t as now } from '@lingui/core/macro'
import {
  useIndexerSettings,
  useSaveIndexerSettings,
  useTestIndexerSettings,
  type IndexerBackend,
} from '../../api/hooks'

export const INDEXER_BACKENDS: { value: IndexerBackend; label: string; placeholder: string }[] = [
  { value: 'prowlarr', label: 'Prowlarr', placeholder: 'http://localhost:9696' },
  { value: 'jackett', label: 'Jackett', placeholder: 'http://localhost:9117' },
]

export function indexerBackendName(backend: IndexerBackend | undefined): string {
  return INDEXER_BACKENDS.find((b) => b.value === backend)?.label ?? 'Prowlarr'
}

const savedToast = () => notifications.show({ message: now`Saved`, color: 'var(--ok)' })

/**
 * The indexer connection: a backend select whose URL and API key follow the chosen backend's stored
 * values. Saving makes the chosen backend the active one.
 */
export function useIndexerForm() {
  const { data: active } = useIndexerSettings()
  const [chosen, setChosen] = useState<IndexerBackend | null>(null)
  const selected = chosen ?? active?.backend
  const { data: saved } = useIndexerSettings(selected)
  const save = useSaveIndexerSettings()
  const test = useTestIndexerSettings()
  const [url, setUrl] = useState('')
  const [apiKey, setApiKey] = useState('')
  const [discarded, discard] = useReducer((n: number) => n + 1, 0)

  useEffect(() => {
    if (saved) {
      setUrl(saved.url ?? '')
      setApiKey(saved.apiKey ?? '')
    }
  }, [saved, discarded])

  const loaded = selected !== undefined && saved?.backend === selected
  const switching = active !== undefined && selected !== undefined && selected !== active.backend
  const dirty =
    saved !== undefined && loaded && (switching || url !== (saved.url ?? '') || apiKey !== (saved.apiKey ?? ''))

  const payload = () => ({ backend: selected as IndexerBackend, url: url || null, apiKey: apiKey || null })
  const name = indexerBackendName(selected)

  return {
    selected,
    switching,
    url,
    apiKey,
    setUrl,
    setApiKey,
    choose: (backend: IndexerBackend) => {
      setChosen(backend)
      setUrl('')
      setApiKey('')
    },
    loaded,
    dirty,
    saving: save.isPending,
    save: () => save.mutate(payload(), { onSuccess: savedToast }),
    commit: () => save.mutateAsync(payload()),
    reset: () => {
      setChosen(null)
      discard()
    },
    testing: test.isPending,
    test: () =>
      test.mutate(
        { backend: selected, url: url || null, apiKey: apiKey || null },
        { onSuccess: () => notifications.show({ message: now`${name} is reachable`, color: 'var(--ok)' }) },
      ),
  }
}

export type IndexerForm = ReturnType<typeof useIndexerForm>
