import { useMemo, useState } from 'react'
import {
  Badge,
  Button,
  Modal,
  Radio,
  ScrollArea,
  Skeleton,
  Stack,
  Text,
  TextInput,
  UnstyledButton,
} from '@mantine/core'
import { IconLinkOff, IconSearch } from '../lib/icons'
import { ModalFooter, ModalHeading } from './ui/ModalShell'
import { EmptyState } from './ui/EmptyState'
import { notifications } from '../lib/toast'
import { Trans, useLingui } from '@lingui/react/macro'
import { plural, t as now } from '@lingui/core/macro'
import { useLinkChapters, useSeriesFiles } from '../api/hooks'
import type { SeriesFileDto } from '../api/types'
import { formatBytes } from '../format'

export function LinkChaptersModal({
  seriesId,
  chapterIds,
  opened,
  onClose,
}: {
  seriesId: number
  chapterIds: number[]
  opened: boolean
  onClose: () => void
}) {
  const { t } = useLingui()
  const { data: files, isLoading } = useSeriesFiles(seriesId, opened)
  const link = useLinkChapters()
  const [picked, setPicked] = useState<string | null>(null)
  const [query, setQuery] = useState('')
  // Files nothing points at yet float to the top: that is almost always the omnibus or the
  // oddly named release the user opened this dialog for.
  const sorted = useMemo(() => {
    const rank = (f: SeriesFileDto) => (!f.onDisk ? 2 : f.status === 'linked' ? 1 : 0)
    return [...(files ?? [])].sort((a, b) => rank(a) - rank(b))
  }, [files])
  const chapterCount = chapterIds.length
  const chapterPhrase = plural(chapterCount, {
    one: '# chapter',
    other: '# chapters',
  })

  const handleClose = () => {
    setPicked(null)
    setQuery('')
    onClose()
  }

  const confirm = (file: SeriesFileDto) => {
    const { fileName } = file
    link.mutate(
      { chapterIds, relativePath: file.relativePath },
      {
        onSuccess: () => {
          notifications.show({
            message: now`Linked ${chapterPhrase} to ${fileName}`,
            color: 'var(--ok)',
          })
          handleClose()
        },
      },
    )
  }

  const needle = query.trim().toLocaleLowerCase()
  const shown = needle ? sorted.filter((f) => f.fileName.toLocaleLowerCase().includes(needle)) : sorted
  const pickedName = files?.find((f) => f.relativePath === picked)?.fileName

  return (
    <Modal
      opened={opened}
      onClose={handleClose}
      title={
        <ModalHeading
          title={t`Link ${chapterPhrase} to a file`}
          description={<Trans>Pick the file in the series folder these chapters are contained in.</Trans>}
        />
      }
      size="lg"
    >
      <Stack gap="sm">
        {isLoading ? (
          <div className="modal-list" aria-busy="true" aria-label={t`Scanning folder…`}>
            {[60, 46, 54].map((w) => (
              <div key={w} className="modal-list-row">
                <Skeleton h={16} w={16} />
                <Skeleton h={12} w={`${w}%`} />
                <Skeleton h={12} w={48} ml="auto" />
              </div>
            ))}
          </div>
        ) : !files || files.length === 0 ? (
          <EmptyState compact mood="asleep" title={t`No files found in the series folder.`} />
        ) : (
          <>
            <TextInput
              size="xs"
              value={query}
              onChange={(e) => setQuery(e.currentTarget.value)}
              leftSection={<IconSearch size={16} />}
              placeholder={t`Find a file in the folder`}
              aria-label={t`Find a file`}
            />
            <ScrollArea.Autosize mah="min(420px, 45dvh)">
              <div className="modal-list">
                {shown.map((f) => (
                  <UnstyledButton
                    key={f.relativePath}
                    className="modal-list-row"
                    data-active={picked === f.relativePath || undefined}
                    onClick={() => setPicked(f.relativePath)}
                    disabled={!f.onDisk || link.isPending}
                  >
                    <Radio.Indicator checked={picked === f.relativePath} size="xs" />
                    <span className="modal-mono" title={f.fileName}>
                      {f.fileName}
                    </span>
                    {f.onDisk && f.status !== 'linked' && (
                      <Badge size="sm" variant="light" color="var(--warn)" leftSection={<IconLinkOff size={11} />}>
                        <Trans>Not linked</Trans>
                      </Badge>
                    )}
                    {f.parsedLabel && (
                      <Badge
                        size="sm"
                        variant="light"
                        color={f.isVolume ? 'var(--volume)' : 'var(--neutral)'}
                        className="tnum"
                      >
                        {f.parsedLabel}
                      </Badge>
                    )}
                    <Text size="sm" c="var(--ink-4)" className="tnum modal-list-size">
                      {formatBytes(f.size)}
                    </Text>
                  </UnstyledButton>
                ))}
                {shown.length === 0 && (
                  <EmptyState compact mood="asleep" title={t`No file matches that.`} />
                )}
              </div>
            </ScrollArea.Autosize>
          </>
        )}
      </Stack>
      <ModalFooter
        status={
          pickedName ? (
            <Trans>
              {chapterPhrase} will read from {pickedName}
            </Trans>
          ) : (
            <Trans>Pick one file</Trans>
          )
        }
      >
        <Button variant="default" onClick={handleClose} disabled={link.isPending}>
          <Trans>Cancel</Trans>
        </Button>
        <Button
          disabled={!picked}
          loading={link.isPending}
          onClick={() => {
            const file = files?.find((f) => f.relativePath === picked)
            if (file) confirm(file)
          }}
        >
          <Trans>Link</Trans>
        </Button>
      </ModalFooter>
    </Modal>
  )
}
