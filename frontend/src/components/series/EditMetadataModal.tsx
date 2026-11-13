import { type ReactNode, useMemo, useState } from 'react'
import {
  ActionIcon,
  Button,
  FileButton,
  Group,
  Input,
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
import { notifications } from '../../lib/toast'
import { IconArrowBackUp, IconLock, IconPlus, IconUpload, IconX } from '../../lib/icons'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import type { LocalizedTitle, MetadataField, SeriesDto } from '../../api/types'
import {
  type EditMetadataRequest,
  useEditSeriesMetadata,
  useResetSeriesMetadata,
  useUploadSeriesCover,
} from '../../api/seriesMetadata'
import { allowedContentRatings, CONTENT_RATING_LABELS, CONTENT_RATINGS } from '../../api/hooks'
import { SERIES_TYPE_LABELS, SERIES_TYPES } from '../../api/readingProfiles'
import { useAuth } from '../../auth/AuthProvider'
import { seriesStatusVisual } from '../ui/status'
import { useLabel } from '../../i18n-context'
import { GENRE_LABELS } from '../CatalogueFilters'
import { ModalFooter, ModalHeading, ModalNote } from '../ui/ModalShell'
import { CoverPicker } from './CoverPicker'

const STATUSES = ['Unknown', 'Ongoing', 'Completed', 'Hiatus', 'Cancelled']
const GENRE_SUGGESTIONS = Object.keys(GENRE_LABELS)
const COVER_TYPES = 'image/jpeg,image/png,image/gif,image/webp'

type EditableField = EditMetadataRequest['fields'][number]

interface Draft {
  title: string
  sortTitle: string
  altTitles: LocalizedTitle[]
  overview: string
  status: string
  totalChapters: number | null
  totalVolumes: number | null
  year: number | null
  type: string | null
  contentRating: string | null
  authorStory: string
  authorArt: string
  publisher: string
  genres: string[]
}

function baseline(series: SeriesDto): Draft {
  return {
    title: series.title,
    sortTitle: series.sortTitle,
    altTitles: series.altTitles ?? [],
    overview: series.overview ?? '',
    status: series.status,
    totalChapters: series.totalChapters,
    totalVolumes: series.totalVolumes,
    year: series.year,
    type: series.type,
    contentRating: series.contentRating,
    authorStory: series.authorStory ?? '',
    authorArt: series.authorArt ?? '',
    publisher: series.publisher ?? '',
    genres: series.genres,
  }
}

function same(a: Draft[keyof Draft], b: Draft[keyof Draft]): boolean {
  return Array.isArray(a) && Array.isArray(b) ? JSON.stringify(a) === JSON.stringify(b) : a === b
}

/** Mirrors the server's `ContentRating.Permits`: an unrated series passes only an unrestricted ceiling. */
function permits(rating: string | null, allowed: string[]): boolean {
  return allowed.length === CONTENT_RATINGS.length || (rating != null && allowed.includes(rating))
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

  const { me } = useAuth()

  const statusOptions = STATUSES.map((status) => ({ value: status, label: renderLabel(seriesStatusVisual(status).label) }))
  const typeOptions = SERIES_TYPES.map((type) => ({ value: type, label: renderLabel(SERIES_TYPE_LABELS[type]) }))
  const allowedRatings = allowedContentRatings(me?.maxContentRating)
  const ratingEditable = permits(series.contentRating, allowedRatings)
  const ratingOptions = (ratingEditable ? allowedRatings : CONTENT_RATINGS).map((rating) => ({
    value: rating,
    label: renderLabel(CONTENT_RATING_LABELS[rating]),
  }))

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
        sortTitle: value('sortTitle'),
        altTitles: value('altTitles'),
        year: value('year'),
        type: value('type'),
        contentRating: value('contentRating'),
        authorStory: value('authorStory'),
        authorArt: value('authorArt'),
        publisher: value('publisher'),
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
        onSuccess: (state) => {
          const restored = state.refreshed && (field !== 'cover' || state.coverRestored)
          notifications.show({
            message: restored
              ? t`Restored from the provider`
              : state.refreshed
                ? t`Unlocked, but the provider's poster could not be downloaded. Your poster stays until a refresh succeeds.`
                : t`Unlocked. The provider's value returns on the next metadata refresh.`,
            color: restored ? 'var(--ok)' : 'var(--warn)',
          })
        },
      },
    )
  }

  const resetButton = (field: MetadataField) =>
    locked(field) ? (
      <Button
        variant="subtle"
        size="compact-xs"
        leftSection={<IconArrowBackUp size={16} />}
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
      {locked(field) && <MetadataLock label />}
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
    <Modal
      opened={opened}
      onClose={close}
      size="lg"
      title={
        <ModalHeading
          title={<Trans>Edit metadata</Trans>}
          description={<Trans>Values you set here win over the provider until you reset them.</Trans>}
        />
      }
    >
      <Stack gap="xs">
        <ModalNote tone="info">
          <Trans>A field you save is locked, so metadata refreshes leave it alone. It applies to everyone who can see this series.</Trans>
        </ModalNote>

        <Group align="flex-start" gap="md" wrap="nowrap" mt="xs">
          {series.coverUrl && (
            <img
              src={series.coverUrl}
              alt=""
              style={{
                width: 72,
                borderRadius: 'var(--mantine-radius-sm)',
                border: '1px solid var(--border)',
                objectFit: 'cover',
                flexShrink: 0,
              }}
            />
          )}
          <Stack gap={4} style={{ flex: 1 }}>
            <Text size="sm" fw={500}>
              {fieldLabel('cover', <Trans>Poster</Trans>)}
            </Text>
            {locked('cover') && (
              <Text size="sm" c="var(--ink-2)">
                <Trans>Set by hand. Metadata refreshes leave it alone.</Trans>
              </Text>
            )}
            <Group gap="xs">
              <FileButton
                accept={COVER_TYPES}
                onChange={(file) => file && upload.mutate(
                  { seriesId: series.id, file },
                  { onSuccess: () => notifications.show({ message: t`Poster replaced`, color: 'var(--ok)' }) },
                )}
              >
                {(props) => (
                  <Button {...props} variant="default" size="xs" leftSection={<IconUpload size={16} />} loading={upload.isPending}>
                    <Trans>Upload image</Trans>
                  </Button>
                )}
              </FileButton>
              {resetButton('cover')}
            </Group>
            <Text size="xs" c="var(--ink-4)">
              <Trans>JPEG, PNG, GIF or WebP, up to 10 MB.</Trans>
            </Text>
            <CoverPicker seriesId={series.id} />
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
          'sortTitle',
          <TextInput
            label={fieldLabel('sortTitle', <Trans>Sort title</Trans>)}
            description={t`Where the series sits in a list sorted by title`}
            value={value('sortTitle')}
            onChange={(e) => set('sortTitle', e.currentTarget.value)}
            maxLength={500}
          />,
        )}

        {row(
          'altTitles',
          <AltTitlesInput
            label={fieldLabel('altTitles', <Trans>Alternative titles</Trans>)}
            value={value('altTitles')}
            onChange={(next) => set('altTitles', next)}
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

        <Group grow align="flex-start">
          {row(
            'year',
            <NumberInput
              label={fieldLabel('year', <Trans>Year</Trans>)}
              value={value('year') ?? ''}
              onChange={(next) => set('year', count(next))}
              min={1800}
              max={2100}
              allowDecimal={false}
              allowNegative={false}
            />,
          )}
          {row(
            'type',
            <Select
              label={fieldLabel('type', <Trans>Type</Trans>)}
              data={typeOptions}
              value={value('type')}
              onChange={(next) => set('type', next)}
              clearable
            />,
          )}
          {row(
            'contentRating',
            <Select
              label={fieldLabel('contentRating', <Trans>Content rating</Trans>)}
              data={ratingOptions}
              value={value('contentRating')}
              onChange={(next) => set('contentRating', next)}
              disabled={!ratingEditable}
              clearable={allowedRatings.length === CONTENT_RATINGS.length}
              allowDeselect={false}
            />,
          )}
        </Group>
        {!ratingEditable && (
          <Text size="xs" c="var(--ink-4)" mt={-6}>
            <Trans>Your content rating limit doesn't cover this series, so you can't change its rating.</Trans>
          </Text>
        )}

        <Group grow align="flex-start">
          {row(
            'authorStory',
            <TextInput
              label={fieldLabel('authorStory', <Trans>Story</Trans>)}
              value={value('authorStory')}
              onChange={(e) => set('authorStory', e.currentTarget.value)}
              maxLength={500}
            />,
          )}
          {row(
            'authorArt',
            <TextInput
              label={fieldLabel('authorArt', <Trans>Art</Trans>)}
              value={value('authorArt')}
              onChange={(e) => set('authorArt', e.currentTarget.value)}
              maxLength={500}
            />,
          )}
          {row(
            'publisher',
            <TextInput
              label={fieldLabel('publisher', <Trans>Publisher</Trans>)}
              value={value('publisher')}
              onChange={(e) => set('publisher', e.currentTarget.value)}
              maxLength={500}
            />,
          )}
        </Group>
        <Text size="xs" c="var(--ink-4)" mt={-6}>
          <Trans>Separate several names with commas.</Trans>
        </Text>

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

        <ModalFooter
          status={
            dirty.length > 0 ? (
              <Plural value={dirty.length} one="# unsaved change" other="# unsaved changes" />
            ) : series.lockedFields.length > 0 ? (
              <Plural value={series.lockedFields.length} one="# field set by hand" other="# fields set by hand" />
            ) : null
          }
        >
          <Button variant="default" onClick={close}>
            <Trans>Cancel</Trans>
          </Button>
          <Button
            onClick={save}
            loading={edit.isPending}
            disabled={
              dirty.length === 0 ||
              (dirty.includes('title') && !value('title').trim()) ||
              (dirty.includes('sortTitle') && !value('sortTitle').trim())
            }
          >
            <Trans>Save</Trans>
          </Button>
        </ModalFooter>
      </Stack>
    </Modal>
  )
}

/** Title rows with an optional language code, the shape `SeriesDto.altTitles` comes in. */
function AltTitlesInput({
  label,
  value,
  onChange,
}: {
  label: ReactNode
  value: LocalizedTitle[]
  onChange: (next: LocalizedTitle[]) => void
}) {
  const { t } = useLingui()
  const update = (index: number, patch: Partial<LocalizedTitle>) =>
    onChange(value.map((row, i) => (i === index ? { ...row, ...patch } : row)))

  return (
    <Input.Wrapper label={label} description={t`The language code picks a title for readers who prefer that language`}>
      <Stack gap={6} mt={6}>
        {value.map((row, index) => (
          <Group key={index} gap={6} wrap="nowrap">
            <TextInput
              style={{ flex: 1 }}
              aria-label={t`Alternative title`}
              value={row.title}
              onChange={(e) => update(index, { title: e.currentTarget.value })}
              maxLength={500}
            />
            <TextInput
              w={84}
              aria-label={t`Language code`}
              placeholder="en"
              value={row.language ?? ''}
              onChange={(e) => update(index, { language: e.currentTarget.value || null })}
              maxLength={20}
            />
            <ActionIcon
              variant="subtle"
              color="gray"
              aria-label={t`Remove title`}
              onClick={() => onChange(value.filter((_, i) => i !== index))}
            >
              <IconX size={16} />
            </ActionIcon>
          </Group>
        ))}
        <Group>
          <Button
            variant="subtle"
            size="compact-xs"
            leftSection={<IconPlus size={16} />}
            onClick={() => onChange([...value, { title: '', language: null }])}
          >
            <Trans>Add title</Trans>
          </Button>
        </Group>
      </Stack>
    </Input.Wrapper>
  )
}

/** Marks a value somebody set by hand, which metadata refreshes leave alone. `label` spells it out beside the icon. */
export function MetadataLock({ size = 14, label = false }: { size?: number; label?: boolean }) {
  const { t } = useLingui()
  if (label) {
    return (
      <span className="metadata-lock-label">
        <IconLock size={size} aria-hidden />
        <Trans>Set by hand</Trans>
      </span>
    )
  }
  return (
    <Tooltip label={t`Set by hand. Metadata refreshes leave it alone.`} withArrow>
      <IconLock
        size={size}
        aria-label={t`Set by hand`}
        style={{ color: 'var(--ink-4)', flexShrink: 0, verticalAlign: 'middle' }}
      />
    </Tooltip>
  )
}
