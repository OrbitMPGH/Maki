import { type ReactNode, useMemo, useState } from 'react'
import {
  Button,
  FileButton,
  Group,
  Modal,
  NumberInput,
  Select,
  Stack,
  TagsInput,
  Text,
  Textarea,
  TextInput,
  Tooltip,
} from '@mantine/core'
import { notifications } from '@mantine/notifications'
import { IconArrowBackUp, IconLock, IconUpload } from '@tabler/icons-react'
import { Trans, useLingui } from '@lingui/react/macro'
import type { MetadataField, SeriesDto } from '../../api/types'
import {
  type EditMetadataRequest,
  useEditSeriesMetadata,
  useResetSeriesMetadata,
  useUploadSeriesCover,
} from '../../api/seriesMetadata'
import { seriesStatusVisual } from '../ui/status'
import { useLabel } from '../../i18n-context'
import { GENRE_LABELS } from '../CatalogueFilters'

const STATUSES = ['Unknown', 'Ongoing', 'Completed', 'Hiatus', 'Cancelled']
const GENRE_SUGGESTIONS = Object.keys(GENRE_LABELS)
const COVER_TYPES = 'image/jpeg,image/png,image/gif,image/webp'

type EditableField = EditMetadataRequest['fields'][number]

interface Draft {
  title: string
  overview: string
  status: string
  totalChapters: number | null
  totalVolumes: number | null
  genres: string[]
}

function baseline(series: SeriesDto): Draft {
  return {
    title: series.title,
    overview: series.overview ?? '',
    status: series.status,
    totalChapters: series.totalChapters,
    totalVolumes: series.totalVolumes,
    genres: series.genres,
  }
}

function same(a: Draft[keyof Draft], b: Draft[keyof Draft]): boolean {
  return Array.isArray(a) && Array.isArray(b) ? a.join('\n') === b.join('\n') : a === b
}

/** `null` for an emptied NumberInput, which hands back an empty string. */
function count(value: string | number): number | null {
  return typeof value === 'number' ? value : null
}

/**
 * Hand-set metadata for one series. A saved field is locked against provider refreshes until
 * "Reset to provider" unlocks it, which also refreshes the series so the provider value returns.
 * <p>
 * The form holds only the fields touched since opening, over the live series. A reset or another
 * save refetches the series, and the untouched fields simply follow it.
 */
export function EditMetadataModal({
  opened,
  onClose,
  series,
}: {
  opened: boolean
  onClose: () => void
  series: SeriesDto
}) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const [edits, setEdits] = useState<Partial<Draft>>({})
  const edit = useEditSeriesMetadata()
  const reset = useResetSeriesMetadata()
  const upload = useUploadSeriesCover()

  const base = useMemo(() => baseline(series), [series])
  const value = <K extends keyof Draft>(key: K): Draft[K] => (edits[key] ?? base[key]) as Draft[K]
  const set = <K extends keyof Draft>(key: K, next: Draft[K]) => setEdits((current) => ({ ...current, [key]: next }))
  const dirty = (Object.keys(edits) as (keyof Draft)[]).filter((key) => !same(edits[key]!, base[key]))
  const locked = (field: MetadataField) => series.lockedFields.includes(field)

  const statusOptions = STATUSES.map((status) => ({ value: status, label: renderLabel(seriesStatusVisual(status).label) }))

  const close = () => {
    setEdits({})
    onClose()
  }

  const save = () => {
    const fields = dirty as EditableField[]
    edit.mutate(
      {
        seriesId: series.id,
        fields,
        title: value('title'),
        overview: value('overview'),
        status: value('status'),
        totalChapters: value('totalChapters'),
        totalVolumes: value('totalVolumes'),
        genres: value('genres'),
      },
      {
        onSuccess: () => {
          notifications.show({ message: t`Metadata saved`, color: 'var(--ok)' })
          close()
        },
      },
    )
  }

  const resetField = (field: MetadataField) => {
    if (field !== 'cover') {
      setEdits((current) => {
        const next = { ...current }
        delete next[field]
        return next
      })
    }
    reset.mutate(
      { seriesId: series.id, fields: [field] },
      {
        onSuccess: (state) =>
          notifications.show({
            message: state.refreshed
              ? t`Restored from the provider`
              : t`Unlocked. The provider's value returns on the next metadata refresh.`,
            color: state.refreshed ? 'var(--ok)' : 'var(--warn)',
          }),
      },
    )
  }

  const resetButton = (field: MetadataField) =>
    locked(field) ? (
      <Button
        variant="subtle"
        size="compact-xs"
        leftSection={<IconArrowBackUp size={14} />}
        loading={reset.isPending && reset.variables?.fields[0] === field}
        disabled={reset.isPending}
        onClick={() => resetField(field)}
      >
        <Trans>Reset to provider</Trans>
      </Button>
    ) : null

  const fieldLabel = (field: MetadataField, label: ReactNode) => (
    <Group gap={6} wrap="nowrap" component="span" style={{ display: 'inline-flex' }}>
      {label}
      {locked(field) && <MetadataLock />}
    </Group>
  )

  const row = (field: EditableField, input: ReactNode) => (
    <Stack gap={2}>
      {input}
      <Group justify="flex-end" h={22}>
        {resetButton(field)}
      </Group>
    </Stack>
  )

  return (
    <Modal opened={opened} onClose={close} title={t`Edit metadata`} size="lg" centered>
      <Stack gap="xs">
        <Text size="sm" c="var(--ink-3)">
          <Trans>
            A field you save is locked, so metadata refreshes leave it alone. It applies to everyone who can see this
            series.
          </Trans>
        </Text>

        <Group align="flex-start" gap="md" wrap="nowrap" mt="xs">
          {series.coverUrl && (
            <img
              src={series.coverUrl}
              alt=""
              style={{ width: 72, borderRadius: 'var(--mantine-radius-sm)', objectFit: 'cover', flexShrink: 0 }}
            />
          )}
          <Stack gap={4} style={{ flex: 1 }}>
            <Text size="sm" fw={500}>
              {fieldLabel('cover', <Trans>Poster</Trans>)}
            </Text>
            <Text size="xs" c="var(--ink-3)">
              <Trans>JPEG, PNG, GIF or WebP, up to 10 MB.</Trans>
            </Text>
            <Group gap="xs">
              <FileButton
                accept={COVER_TYPES}
                onChange={(file) => file && upload.mutate(
                  { seriesId: series.id, file },
                  { onSuccess: () => notifications.show({ message: t`Poster replaced`, color: 'var(--ok)' }) },
                )}
              >
                {(props) => (
                  <Button {...props} variant="default" size="xs" leftSection={<IconUpload size={14} />} loading={upload.isPending}>
                    <Trans>Upload image</Trans>
                  </Button>
                )}
              </FileButton>
              {resetButton('cover')}
            </Group>
          </Stack>
        </Group>

        {row(
          'title',
          <TextInput
            label={fieldLabel('title', <Trans>Title</Trans>)}
            value={value('title')}
            onChange={(e) => set('title', e.currentTarget.value)}
            maxLength={500}
          />,
        )}

        {row(
          'overview',
          <Textarea
            label={fieldLabel('overview', <Trans>Synopsis</Trans>)}
            value={value('overview')}
            onChange={(e) => set('overview', e.currentTarget.value)}
            autosize
            minRows={4}
            maxRows={12}
          />,
        )}

        <Group grow align="flex-start">
          {row(
            'status',
            <Select
              label={fieldLabel('status', <Trans>Status</Trans>)}
              data={statusOptions}
              value={value('status')}
              onChange={(next) => next && set('status', next)}
              allowDeselect={false}
            />,
          )}
          {row(
            'totalChapters',
            <NumberInput
              label={fieldLabel('totalChapters', <Trans>Total chapters</Trans>)}
              value={value('totalChapters') ?? ''}
              onChange={(next) => set('totalChapters', count(next))}
              min={0}
              max={100000}
              allowDecimal={false}
              allowNegative={false}
            />,
          )}
          {row(
            'totalVolumes',
            <NumberInput
              label={fieldLabel('totalVolumes', <Trans>Total volumes</Trans>)}
              value={value('totalVolumes') ?? ''}
              onChange={(next) => set('totalVolumes', count(next))}
              min={0}
              max={100000}
              allowDecimal={false}
              allowNegative={false}
            />,
          )}
        </Group>

        {row(
          'genres',
          <TagsInput
            label={fieldLabel('genres', <Trans>Genres</Trans>)}
            description={t`Press Enter to add a genre`}
            data={GENRE_SUGGESTIONS}
            value={value('genres')}
            onChange={(next) => set('genres', next)}
            clearable
          />,
        )}

        <Group justify="flex-end" mt="sm">
          <Button variant="default" onClick={close}>
            <Trans>Cancel</Trans>
          </Button>
          <Button
            onClick={save}
            loading={edit.isPending}
            disabled={dirty.length === 0 || (dirty.includes('title') && !value('title').trim())}
          >
            <Trans>Save</Trans>
          </Button>
        </Group>
      </Stack>
    </Modal>
  )
}

/** Marks a value somebody set by hand, which metadata refreshes leave alone. */
export function MetadataLock({ size = 14 }: { size?: number }) {
  const { t } = useLingui()
  return (
    <Tooltip label={t`Set by hand. Metadata refreshes leave it alone.`} withArrow>
      <IconLock
        size={size}
        stroke={1.8}
        aria-label={t`Set by hand`}
        style={{ color: 'var(--ink-4)', flexShrink: 0, verticalAlign: 'middle' }}
      />
    </Tooltip>
  )
}
