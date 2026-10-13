import { useState } from 'react'
import {
  Button,
  Divider,
  Group,
  Image,
  Loader,
  Modal,
  ScrollArea,
  Stack,
  Text,
  TextInput,
  UnstyledButton,
} from '@mantine/core'
import { useDebouncedValue } from '@mantine/hooks'
import { useMutation } from '@tanstack/react-query'
import { Trans, useLingui } from '@lingui/react/macro'
import { api } from '../../api/client'
import { errorText } from '../../api/errorText'
import { useMetadataSearch } from '../../api/hooks'
import type { MetadataSearchResult } from '../../api/types'

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

  return (
    <Modal opened onClose={onClose} title={t`Find a match for ${folderName}`} size="lg">
      <Stack gap="sm">
        <TextInput
          label={t`Search by title`}
          value={query}
          onChange={(e) => setQuery(e.currentTarget.value)}
          rightSection={search.isFetching ? <Loader size="xs" /> : null}
          data-autofocus
        />
        <ScrollArea.Autosize mah={320}>
          <Stack gap={4}>
            {debounced.trim().length > 1 && !search.isFetching && results.length === 0 && (
              <Text size="sm" c="var(--ink-3)">
                <Trans>Nothing found for that title.</Trans>
              </Text>
            )}
            {results.map((r) => {
              const { title, year } = r
              return (
                <UnstyledButton
                  key={r.providerId}
                  onClick={() => onPick(r)}
                  className="import-match-option"
                  aria-label={t`Use ${title}`}
                >
                  <Group wrap="nowrap" gap="sm">
                    {r.coverUrl ? (
                      <Image src={r.coverUrl} w={32} h={48} radius="sm" fit="cover" alt="" />
                    ) : (
                      <div style={{ width: 32, height: 48 }} />
                    )}
                    <div style={{ minWidth: 0 }}>
                      <Text size="sm" fw={600} lineClamp={1}>
                        {title}
                      </Text>
                      {year && (
                        <Text size="xs" c="var(--ink-3)">
                          {year}
                        </Text>
                      )}
                    </div>
                  </Group>
                </UnstyledButton>
              )
            })}
          </Stack>
        </ScrollArea.Autosize>
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
      </Stack>
    </Modal>
  )
}
