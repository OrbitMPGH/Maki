import { useState } from 'react'
import {
  Button,
  Divider,
  Group,
  Modal,
  ScrollArea,
  Stack,
  Text,
  TextInput,
  UnstyledButton,
} from '@mantine/core'
import { useDebouncedValue } from '@mantine/hooks'
import { useMutation } from '@tanstack/react-query'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { api } from '../../api/client'
import { errorText } from '../../api/errorText'
import { useMetadataSearch } from '../../api/hooks'
import type { MetadataSearchResult } from '../../api/types'
import { IconChevronRight } from '../../lib/icons'
import { EmptyState } from '../ui/EmptyState'
import { ModalFooter, ModalHeading } from '../ui/ModalShell'
import { StatusDot } from '../ui/StatusDot'
import { Thumb } from '../ui/Thumb'
import { MODAL_SIZE } from '../ui/modalSizes'

/**
 * Picks the match for one import folder by hand: a title search (the same endpoint the Add dialog
 * uses) or a pasted MangaBaka id, or a MangaBaka, AniList or MyAnimeList link, which the server
 * resolves to the MangaBaka series.
 */
export function ImportMatchFinder({
  folderName,
  initialQuery,
  onPick,
  onClose,
}: {
  folderName: string
  initialQuery: string
  onPick: (match: MetadataSearchResult) => void
  onClose: () => void
}) {
  const { t } = useLingui()
  const [query, setQuery] = useState(initialQuery)
  const [debounced] = useDebouncedValue(query, 400)
  const [pasted, setPasted] = useState('')
  const search = useMetadataSearch(debounced)
  const resolve = useMutation({
    mutationFn: (value: string) =>
      api<MetadataSearchResult>(`/libraryimport/resolve?value=${encodeURIComponent(value)}`),
    onSuccess: onPick,
    meta: { silent: true },
  })
  const results = search.data ?? []

  const showEmpty = debounced.trim().length > 1 && !search.isFetching && results.length === 0

  return (
    <Modal
      opened
      onClose={onClose}
      title={
        <ModalHeading
          title={t`Find a match for ${folderName}`}
          description={<Trans>Search by title, or paste an id or link.</Trans>}
        />
      }
      size={MODAL_SIZE.default}
    >
      <Stack gap="sm">
        <TextInput
          label={t`Search by title`}
          value={query}
          onChange={(e) => setQuery(e.currentTarget.value)}
          rightSection={search.isFetching ? <StatusDot tone="info" live aria-hidden /> : null}
          data-autofocus
        />
        {showEmpty && <EmptyState compact mood="asking" title={t`Nothing found for that title.`} />}
        {results.length > 0 && (
          <ScrollArea.Autosize mah={320} mx={-10} scrollbars="y">
            <div className="modal-list" style={{ marginInline: 10 }}>
              {results.map((r) => (
                <UnstyledButton key={r.providerId} onClick={() => onPick(r)} className="modal-list-row">
                  <Thumb src={r.coverUrl} size="sm" />
                  <div style={{ minWidth: 0, flex: 1 }}>
                    <Text size="sm" fw={600} lineClamp={1}>
                      {r.title}
                    </Text>
                    {r.year && (
                      <Text size="xs" c="var(--ink-3)">
                        {r.year}
                      </Text>
                    )}
                  </div>
                  <IconChevronRight size={16} color="var(--ink-4)" aria-hidden />
                </UnstyledButton>
              ))}
            </div>
          </ScrollArea.Autosize>
        )}
        <Divider label={t`or paste an id or link`} labelPosition="center" />
        <form
          onSubmit={(e) => {
            e.preventDefault()
            if (pasted.trim()) resolve.mutate(pasted.trim())
          }}
        >
          <Group align="flex-end" wrap="nowrap">
            <TextInput
              style={{ flex: 1 }}
              label={t`MangaBaka id, or a MangaBaka, AniList or MyAnimeList link`}
              placeholder="https://anilist.co/manga/30013"
              value={pasted}
              onChange={(e) => {
                setPasted(e.currentTarget.value)
                resolve.reset()
              }}
              error={resolve.isError ? errorText(resolve.error) : undefined}
            />
            <Button type="submit" loading={resolve.isPending} disabled={!pasted.trim()}>
              <Trans>Use</Trans>
            </Button>
          </Group>
        </form>
        <ModalFooter status={<Plural value={results.length} one="# result" other="# results" />}>
          <Button variant="default" onClick={onClose}>
            <Trans>Cancel</Trans>
          </Button>
        </ModalFooter>
      </Stack>
    </Modal>
  )
}
