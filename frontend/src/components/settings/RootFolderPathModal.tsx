import { useState, type FormEvent } from 'react'
import { Button, Group, Modal, Stack, Text, TextInput } from '@mantine/core'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { t as now } from '@lingui/core/macro'
import { IconPencil } from '../../lib/icons'
import { notifications } from '../../lib/toast'
import { errorText } from '../../api/errorText'
import { ApiError } from '../../api/client'
import { useChangeRootFolderPath, useCheckRootFolderPath, type RootFolderPathCheck } from '../../api/hooks'
import type { RootFolder } from '../../api/types'
import { MonoValue } from '../ui/MonoValue'
import { ModalFooter, ModalHeading, ModalNote } from '../ui/ModalShell'
import { FolderBrowserModal } from './FolderBrowserModal'
import { formatNumber } from '../../format'

function BusyNote({ code }: { code: RootFolderPathCheck['busy'][number] }) {
  switch (code) {
    case 'downloads':
      return <Trans>A download is writing into this folder.</Trans>
    case 'imports':
      return <Trans>An import is still linking files in this folder.</Trans>
    case 'moves':
      return <Trans>A bulk move or rename is running.</Trans>
    case 'scans':
      return <Trans>A health scan or repair is running in this folder.</Trans>
  }
}

function WarningNote({ code }: { code: RootFolderPathCheck['warnings'][number] }) {
  switch (code) {
    case 'kavitaPathMap':
      return <Trans>The Kavita path mapping still names the old path. Update it in Settings after saving.</Trans>
    case 'downloadPathMap':
      return (
        <Trans>The download client path mapping points inside the old path. Update it in Settings after saving.</Trans>
      )
    case 'overlapsRoot':
      return <Trans>The new path is inside another root folder, or contains one.</Trans>
  }
}

/**
 * Points a root folder at a new path after a re-mount or a move to new storage. Nothing on disk is
 * moved: the dry run says how many series folders are already there, and Save is held until it has
 * run for exactly the path in the field.
 */
export function RootFolderPathModal({ folder, onClose }: { folder: RootFolder | null; onClose: () => void }) {
  const { t } = useLingui()
  const [path, setPath] = useState('')
  const [browsing, setBrowsing] = useState(false)
  const [checked, setChecked] = useState<{ input: string; result: RootFolderPathCheck } | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busyOnSave, setBusyOnSave] = useState<RootFolderPathCheck['busy']>([])
  const check = useCheckRootFolderPath()
  const save = useChangeRootFolderPath()
  const [openedFor, setOpenedFor] = useState<number | null>(null)

  if (folder && openedFor !== folder.id) {
    setOpenedFor(folder.id)
    setPath(folder.path)
    setChecked(null)
    setError(null)
    setBusyOnSave([])
  }

  const input = path.trim()
  const result = checked?.input === input ? checked.result : null
  const busy = busyOnSave.length > 0 ? busyOnSave : (result?.busy ?? [])
  const unchanged = result !== null && result.path === result.oldPath

  const runCheck = () => {
    if (!folder || !input) return
    setError(null)
    setBusyOnSave([])
    check.mutate(
      { id: folder.id, path: input },
      {
        onSuccess: (r) => setChecked({ input, result: r }),
        onError: (err) => {
          setChecked(null)
          setError(errorText(err))
        },
      },
    )
  }

  const runSave = () => {
    if (!folder || !result) return
    save.mutate(
      { id: folder.id, path: input },
      {
        onSuccess: () => {
          notifications.show({ color: 'var(--ok)', message: now`Root folder path saved` })
          setOpenedFor(null)
          onClose()
        },
        onError: (err) => {
          const payload = err instanceof ApiError ? (err.payload as { busy?: RootFolderPathCheck['busy'] } | null) : null
          if (payload?.busy?.length) setBusyOnSave(payload.busy)
          else setError(errorText(err))
        },
      },
    )
  }

  const close = () => {
    setOpenedFor(null)
    onClose()
  }

  const found = result ? formatNumber(result.foldersFound) : ''
  const total = result ? formatNumber(result.series) : ''
  const more = result ? result.foldersMissing - result.missingFolders.length : 0

  return (
    <>
      <Modal
        opened={folder !== null && !browsing}
        onClose={close}
        centered
        size="lg"
        title={
          <ModalHeading
            title={<Trans>Change root folder path</Trans>}
            description={<Trans>For a re-mounted share or a library moved to new storage. Files are not moved.</Trans>}
          />
        }
      >
        <Stack gap="sm">
          <div>
            <Text size="xs" c="var(--ink-3)" mb={4}>
              <Trans>Current path</Trans>
            </Text>
            <MonoValue value={folder?.path ?? ''} copy minWidth="10rem" />
          </div>
          <Group
            component="form"
            align="flex-end"
            onSubmit={(e: FormEvent) => {
              e.preventDefault()
              runCheck()
            }}
          >
            <TextInput
              label={t`New path`}
              value={path}
              onChange={(e) => setPath(e.currentTarget.value)}
              style={{ flex: '1 1 16rem' }}
            />
            <Button variant="default" onClick={() => setBrowsing(true)}>
              <Trans>Browse</Trans>
            </Button>
            <Button type="submit" variant="default" loading={check.isPending} disabled={!input}>
              <Trans>Check</Trans>
            </Button>
          </Group>

          {error && <ModalNote tone="danger">{error}</ModalNote>}

          {result && (
            <>
              {result.series === 0 ? (
                <ModalNote tone="info">
                  <Trans>No series use this root folder yet.</Trans>
                </ModalNote>
              ) : result.foldersMissing === 0 ? (
                <ModalNote tone="ok">
                  <Trans>All {total} series folders are there.</Trans>
                </ModalNote>
              ) : (
                <ModalNote tone="warn">
                  <Stack gap={4}>
                    <span>
                      <Trans>
                        {found} of {total} series folders are there. The others will show their files as
                        missing until their folders are in place.
                      </Trans>
                    </span>
                    <Text size="xs" ff="monospace" component="span">
                      {result.missingFolders.join(', ')}
                    </Text>
                    {more > 0 && (
                      <Text size="xs" c="var(--ink-3)" component="span">
                        <Plural value={more} one="and # more" other="and # more" />
                      </Text>
                    )}
                  </Stack>
                </ModalNote>
              )}
              {result.warnings.map((code) => (
                <ModalNote key={code} tone="warn">
                  <WarningNote code={code} />
                </ModalNote>
              ))}
            </>
          )}

          {busy.length > 0 && (
            <ModalNote tone="danger">
              <Stack gap={4}>
                {busy.map((code) => (
                  <span key={code}>
                    <BusyNote code={code} />
                  </span>
                ))}
                <span>
                  <Trans>Try again once it has finished.</Trans>
                </span>
              </Stack>
            </ModalNote>
          )}

          <ModalFooter
            status={
              !result && !error ? (
                <Text size="xs" c="var(--ink-3)">
                  <Trans>Check the new path before saving.</Trans>
                </Text>
              ) : undefined
            }
          >
            <Button variant="default" onClick={close}>
              <Trans>Cancel</Trans>
            </Button>
            <Button
              leftSection={<IconPencil size={16} />}
              loading={save.isPending}
              disabled={!result || unchanged || busy.length > 0}
              onClick={runSave}
            >
              <Trans>Save path</Trans>
            </Button>
          </ModalFooter>
        </Stack>
      </Modal>

      <FolderBrowserModal
        opened={browsing}
        onClose={() => setBrowsing(false)}
        onSelect={setPath}
        initialPath={path}
      />
    </>
  )
}
