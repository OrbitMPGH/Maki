import { useEffect, useState } from 'react'
import {
  ActionIcon,
  Button,
  Group,
  Loader,
  Modal,
  ScrollArea,
  Stack,
  Text,
  TextInput,
  UnstyledButton,
} from '@mantine/core'
import { IconArrowUp, IconFolder, IconServer } from '@tabler/icons-react'
import { Trans, useLingui } from '@lingui/react/macro'
import { useBrowseFolders } from '../../api/hooks'

/**
 * Walks the server's filesystem (not the browser's) so an admin can pick a root folder instead of
 * typing its path. Opens at `initialPath` when it names a folder, else at the top.
 */
export function FolderBrowserModal({
  opened,
  onClose,
  onSelect,
  initialPath,
}: {
  opened: boolean
  onClose: () => void
  onSelect: (path: string) => void
  initialPath?: string
}) {
  const { t } = useLingui()
  const [path, setPath] = useState<string | null>(null)
  const [previous, setPrevious] = useState('')
  const [typed, setTyped] = useState('')
  const { data, error, isFetching, isPlaceholderData } = useBrowseFolders(opened ? path : null)

  useEffect(() => {
    if (!opened) return
    const start = initialPath?.trim() ?? ''
    setPath(start)
    setPrevious('')
    setTyped(start)
  }, [opened, initialPath])

  useEffect(() => {
    if (data && !isPlaceholderData) setTyped(data.path)
  }, [data, isPlaceholderData])

  const go = (next: string) => {
    if (data && !isPlaceholderData) setPrevious(data.path)
    setPath(next)
    setTyped(next)
  }

  const listing = error ? null : data
  const atDriveList = listing?.path === ''
  const current = listing && !isPlaceholderData ? listing.path : ''

  return (
    <Modal opened={opened} onClose={onClose} title={t`Choose a folder`} size="lg">
      <Stack gap="sm">
        <Group gap="xs" wrap="nowrap">
          <ActionIcon
            variant="default"
            size="lg"
            onClick={() => listing?.parent != null && go(listing.parent)}
            disabled={listing?.parent == null}
            aria-label={t`Up one folder`}
          >
            <IconArrowUp size={16} />
          </ActionIcon>
          <TextInput
            aria-label={t`Folder path`}
            value={typed}
            onChange={(e) => setTyped(e.currentTarget.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') {
                e.preventDefault()
                go(typed.trim())
              }
            }}
            rightSection={isFetching ? <Loader size="xs" /> : null}
            ff="monospace"
            style={{ flex: 1 }}
          />
        </Group>

        <ScrollArea h={320} className="folder-browser-list" type="auto">
          {error ? (
            <Stack gap="xs" p="md" align="flex-start">
              <Text size="sm" c="var(--danger)">{error.message}</Text>
              <Button size="xs" variant="default" onClick={() => go(previous)}>
                <Trans>Go back</Trans>
              </Button>
            </Stack>
          ) : listing && listing.folders.length === 0 ? (
            <Text size="sm" c="var(--ink-3)" p="md">
              <Trans>No folders in here.</Trans>
            </Text>
          ) : (
            listing?.folders.map((f) => (
              <UnstyledButton
                key={f.path}
                className="folder-browser-row"
                onClick={() => go(f.path)}
                disabled={isPlaceholderData}
              >
                {atDriveList ? <IconServer size={16} /> : <IconFolder size={16} />}
                <Text span size="sm" className="folder-browser-name">{f.name}</Text>
              </UnstyledButton>
            ))
          )}
        </ScrollArea>

        <Group justify="flex-end" gap="xs">
          <Button variant="default" onClick={onClose}>
            <Trans>Cancel</Trans>
          </Button>
          <Button
            onClick={() => {
              onSelect(current)
              onClose()
            }}
            disabled={!current}
          >
            <Trans>Use this folder</Trans>
          </Button>
        </Group>
      </Stack>
    </Modal>
  )
}
