import { useMemo, useState } from 'react'
import { Alert, Button, Group, Loader, Modal, NumberInput, Stack, Text, TextInput } from '@mantine/core'
import { IconArrowLeft, IconSearch } from '@tabler/icons-react'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { useChapters, useSeries } from '../../api/hooks'
import type { SeriesDto } from '../../api/types'
import { usePageConversionSample, type PageConversionSample } from '../../api/pageConversion'
import { chapterLabel } from '../series/chapterTable'

const MAX_SERIES_ROWS = 40

interface PickedFile {
  fileId: number
  label: string
}

/**
 * Chooses a page to compare when the three suggestions do not show what the admin cares about:
 * a series, then one of its chapters that has a file, then a page number inside that file.
 */
export function ComparePicker({
  opened,
  onClose,
  onPick,
}: {
  opened: boolean
  onClose: () => void
  onPick: (sample: PageConversionSample) => void
}) {
  const { t } = useLingui()
  const [series, setSeries] = useState<SeriesDto | null>(null)
  const [file, setFile] = useState<PickedFile | null>(null)

  const close = () => {
    setSeries(null)
    setFile(null)
    onClose()
  }

  return (
    <Modal opened={opened} onClose={close} title={t`Pick another page`} size="lg" centered>
      {!series && <SeriesStage onPick={setSeries} />}
      {series && !file && <ChapterStage series={series} onBack={() => setSeries(null)} onPick={setFile} />}
      {series && file && (
        <PageStage
          file={file}
          onBack={() => setFile(null)}
          onPick={(sample) => {
            onPick(sample)
            close()
          }}
        />
      )}
    </Modal>
  )
}

function SeriesStage({ onPick }: { onPick: (series: SeriesDto) => void }) {
  const { t } = useLingui()
  const { data: all, isLoading } = useSeries()
  const [query, setQuery] = useState('')

  const rows = useMemo(() => {
    const needle = query.trim().toLocaleLowerCase()
    const matches = (all ?? []).filter(
      (s) =>
        needle === '' ||
        s.displayTitle.toLocaleLowerCase().includes(needle) ||
        s.title.toLocaleLowerCase().includes(needle),
    )
    return matches.sort((a, b) => a.sortTitle.localeCompare(b.sortTitle))
  }, [all, query])

  return (
    <Stack gap="sm">
      <TextInput
        autoFocus
        data-autofocus
        aria-label={t`Search your series`}
        placeholder={t`Search your series`}
        leftSection={<IconSearch size={16} />}
        value={query}
        onChange={(event) => setQuery(event.currentTarget.value)}
      />
      {isLoading && <Loader size="sm" />}
      <div className="pc-pick-list">
        <Stack gap={2}>
          {rows.slice(0, MAX_SERIES_ROWS).map((s) => (
            <Button key={s.id} variant="subtle" color="gray" justify="flex-start" onClick={() => onPick(s)}>
              {s.displayTitle}
            </Button>
          ))}
        </Stack>
      </div>
      {!isLoading && rows.length === 0 && (
        <Text size="sm" c="var(--ink-3)">
          <Trans>No series match that search.</Trans>
        </Text>
      )}
      {rows.length > MAX_SERIES_ROWS && (
        <Text size="xs" c="var(--ink-3)">
          <Trans>Showing the first {MAX_SERIES_ROWS}. Type more of the title to narrow it down.</Trans>
        </Text>
      )}
    </Stack>
  )
}

function ChapterStage({
  series,
  onBack,
  onPick,
}: {
  series: SeriesDto
  onBack: () => void
  onPick: (file: PickedFile) => void
}) {
  const { data: chapters, isLoading } = useChapters(series.id)

  const files = useMemo(() => {
    const byFile = new Map<number, { file: PickedFile; pages: number | null; format: string | null; number: number }>()
    for (const c of chapters ?? []) {
      const q = c.fileQuality
      if (!c.hasFile || !q || q.imageFormat === 'pdf' || byFile.has(q.fileId)) continue
      byFile.set(q.fileId, {
        file: { fileId: q.fileId, label: chapterLabel(c) },
        pages: q.pageCount,
        format: q.imageFormat,
        number: c.number ?? c.volume ?? 0,
      })
    }
    return [...byFile.values()].sort((a, b) => a.number - b.number)
  }, [chapters])

  return (
    <Stack gap="sm">
      <Group gap="xs" wrap="nowrap">
        <Button variant="subtle" color="gray" size="xs" leftSection={<IconArrowLeft size={14} />} onClick={onBack}>
          <Trans>Back</Trans>
        </Button>
        <Text fw={600} truncate="end">
          {series.displayTitle}
        </Text>
      </Group>
      {isLoading && <Loader size="sm" />}
      <div className="pc-pick-list">
        <Stack gap={2}>
          {files.map(({ file, pages, format }) => (
            <Button
              key={file.fileId}
              variant="subtle"
              color="gray"
              justify="space-between"
              fullWidth
              onClick={() => onPick(file)}
              rightSection={
                <Text size="xs" c="var(--ink-3)">
                  <FileMeta pages={pages} format={format} />
                </Text>
              }
            >
              {file.label}
            </Button>
          ))}
        </Stack>
      </div>
      {!isLoading && files.length === 0 && (
        <Text size="sm" c="var(--ink-3)">
          <Trans>This series has no chapter files to compare.</Trans>
        </Text>
      )}
    </Stack>
  )
}

function FileMeta({ pages, format }: { pages: number | null; format: string | null }) {
  const name = format?.toUpperCase() ?? ''
  if (pages === null) return format ? <Trans>Not measured yet, {name}</Trans> : <Trans>Not measured yet</Trans>
  if (!format) return <Plural value={pages} one="# page" other="# pages" />
  return <Plural value={pages} one={`# page, ${name}`} other={`# pages, ${name}`} />
}

function PageStage({
  file,
  onBack,
  onPick,
}: {
  file: PickedFile
  onBack: () => void
  onPick: (sample: PageConversionSample) => void
}) {
  const { t } = useLingui()
  const { data: sample, isLoading, isError } = usePageConversionSample(file.fileId)
  const [typed, setTyped] = useState<number | string>('')
  const suggested = sample ? sample.page + 1 : 1
  const pageNumber = typeof typed === 'number' ? typed : suggested
  const pageCount = sample?.pageCount ?? 1
  const valid = pageNumber >= 1 && pageNumber <= pageCount

  return (
    <Stack gap="sm">
      <Group gap="xs" wrap="nowrap">
        <Button variant="subtle" color="gray" size="xs" leftSection={<IconArrowLeft size={14} />} onClick={onBack}>
          <Trans>Back</Trans>
        </Button>
        <Text fw={600} truncate="end">
          {file.label}
        </Text>
      </Group>
      {isLoading && <Loader size="sm" />}
      {isError && (
        <Alert color="red" variant="light">
          <Trans>Maki can only preview pages from CBZ and ZIP files, and could not open this one.</Trans>
        </Alert>
      )}
      {sample && (
        <>
          <NumberInput
            label={t`Page`}
            description={t`Pages 1 to ${pageCount}. The suggested page is ${suggested}, from the middle of the file.`}
            min={1}
            max={pageCount}
            allowDecimal={false}
            clampBehavior="strict"
            value={typed === '' ? suggested : typed}
            onChange={setTyped}
          />
          <Group justify="flex-end">
            <Button disabled={!valid} onClick={() => onPick({ ...sample, page: pageNumber - 1, reason: 'custom' })}>
              <Trans>Compare this page</Trans>
            </Button>
          </Group>
        </>
      )}
    </Stack>
  )
}
