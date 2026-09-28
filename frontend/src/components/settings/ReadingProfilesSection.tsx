import { useState } from 'react'
import {
  ActionIcon,
  Badge,
  Button,
  Card,
  Group,
  MultiSelect,
  NumberInput,
  Select,
  Stack,
  Switch,
  Text,
  TextInput,
  Title,
  Tooltip,
} from '@mantine/core'
import { notifications } from '@mantine/notifications'
import { IconChevronDown, IconChevronUp, IconPlus, IconTrash } from '@tabler/icons-react'
import {
  SERIES_TYPES,
  SERIES_TYPE_LABELS,
  useCreateReadingProfile,
  useDeleteReadingProfile,
  useReadingProfiles,
  useUpdateReadingProfile,
  type ReadingProfile,
  type ReadingProfileInput,
} from '../../api/readingProfiles'
import { BACKGROUNDS, DEFAULT_PREFS, type ReaderPrefs } from '../../pages/reader/prefs'
import { useReaderSettings, useSaveReaderSettings } from '../../api/reader'
import { ConfirmDialog } from '../ui/ConfirmDialog'
import { Panel } from '../ui/Panel'
import { useLabel } from '../../i18n-context'
import { Trans, useLingui } from '@lingui/react/macro'
import { msg, t as now } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'

const MODE_LABELS: Record<ReaderPrefs['mode'], MessageDescriptor> = {
  paged: msg`Single page`,
  double: msg`Two pages`,
  vertical: msg`Continuous`,
}

const DIRECTION_LABELS: Record<ReaderPrefs['direction'], MessageDescriptor> = {
  ltr: msg`left to right`,
  rtl: msg`right to left`,
}

const FIT_LABELS: Record<ReaderPrefs['fit'], MessageDescriptor> = {
  width: msg`fit width`,
  height: msg`fit height`,
  screen: msg`fit screen`,
  original: msg`1:1`,
}

/** `renderLabel` comes from the caller's own `useLabel()` so this stays a plain function, not a hook. */
function summarize(prefs: ReaderPrefs, renderLabel: (label: MessageDescriptor) => string): string {
  return `${renderLabel(MODE_LABELS[prefs.mode])}, ${renderLabel(DIRECTION_LABELS[prefs.direction])}, ${renderLabel(FIT_LABELS[prefs.fit])}`
}

/**
 * The built-in reader's settings: the Default every series falls back to, then the user's named
 * presets and which series types each one is picked for automatically. Default is edited with the
 * same form as a profile, so the two can't drift apart in which options they offer.
 * <p>
 * The type claim is the whole point: a manhwa opens as a continuous left-to-right strip and a manga
 * stays single-page right-to-left with nothing configured per series. A type belongs to at most one
 * profile, so the server refuses a second claimant rather than silently picking one.
 */
export function ReadingProfilesSection() {
  const { t } = useLingui()
  const { data: profiles } = useReadingProfiles()
  const create = useCreateReadingProfile()
  const [creating, setCreating] = useState(false)

  return (
    <Panel>
      <Group justify="space-between" mb="sm">
        <Title order={4}>
          <Trans>Reader</Trans>
        </Title>
        <Button
          size="xs"
          variant="light"
          leftSection={<IconPlus size={14} />}
          onClick={() => setCreating((open) => !open)}
        >
          <Trans>New profile</Trans>
        </Button>
      </Group>

      <Text size="sm" c="var(--ink-3)" mb="md">
        <Trans>
          How the built-in reader opens a series. A profile applies automatically to the series
          types it covers; everything else uses Default, including series whose metadata hasn't
          been refreshed since upgrading. You can pin a profile or override settings from inside
          the reader.
        </Trans>
      </Text>

      {creating && (
        <ProfileEditor
          key="new"
          initial={{ name: '', prefs: DEFAULT_PREFS, seriesTypes: [] }}
          taken={(profiles ?? []).flatMap((p) => p.seriesTypes)}
          submitLabel={t`Create`}
          busy={create.isPending}
          onCancel={() => setCreating(false)}
          onSubmit={(input) =>
            create.mutate(input, {
              onSuccess: () => {
                setCreating(false)
                notifications.show({ message: now`Profile created`, color: 'var(--ok)' })
              },
            })
          }
        />
      )}

      <Stack gap="xs" mt={creating ? 'md' : undefined}>
        <DefaultRow />
        {(profiles ?? []).map((profile) => (
          <ProfileRow key={profile.id} profile={profile} all={profiles ?? []} />
        ))}
      </Stack>
    </Panel>
  )
}

/** The fallback under every profile. Stored with the reader settings, not as a profile row. */
function DefaultRow() {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const [open, setOpen] = useState(false)
  const { data: settings } = useReaderSettings()
  const save = useSaveReaderSettings()
  const prefs = settings?.defaults ?? DEFAULT_PREFS
  const name = t`Default`

  return (
    <Card withBorder radius="sm" padding="xs">
      <Group justify="space-between" wrap="nowrap">
        <div style={{ minWidth: 0 }}>
          <Group gap="xs" wrap="nowrap">
            <Text fw={600} fz="sm" truncate>
              {name}
            </Text>
            <Badge size="xs" variant="outline" color="var(--neutral)">
              <Trans>Everything else</Trans>
            </Badge>
          </Group>
          <Text fz="xs" c="var(--ink-3)">
            {summarize(prefs, renderLabel)}
          </Text>
        </div>
        <ActionIcon
          variant="subtle"
          color="var(--neutral)"
          onClick={() => setOpen((value) => !value)}
          aria-label={open ? t`Collapse` : t`Edit profile`}
          disabled={!settings}
        >
          {open ? <IconChevronUp size={16} /> : <IconChevronDown size={16} />}
        </ActionIcon>
      </Group>

      {open && settings && (
        <ProfileEditor
          fixed
          initial={{ name, prefs, seriesTypes: [] }}
          taken={[]}
          submitLabel={t`Save`}
          busy={save.isPending}
          onCancel={() => setOpen(false)}
          onSubmit={(input) =>
            save.mutate(
              { defaults: input.prefs, pushToKavita: settings.pushToKavita },
              { onSuccess: () => notifications.show({ message: now`Saved`, color: 'var(--ok)' }) },
            )
          }
        />
      )}
    </Card>
  )
}

function ProfileRow({ profile, all }: { profile: ReadingProfile; all: ReadingProfile[] }) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const [open, setOpen] = useState(false)
  const update = useUpdateReadingProfile()
  const remove = useDeleteReadingProfile()
  const [confirming, setConfirming] = useState(false)
  const { name } = profile

  return (
    <Card withBorder radius="sm" padding="xs">
      <Group justify="space-between" wrap="nowrap">
        <div style={{ minWidth: 0 }}>
          <Group gap="xs" wrap="nowrap">
            <Text fw={600} fz="sm" truncate>
              {profile.name}
            </Text>
            {profile.seriesTypes.map((type) => (
              <Badge key={type} size="xs" variant="light">
                {renderLabel(SERIES_TYPE_LABELS[type] ?? type)}
              </Badge>
            ))}
          </Group>
          <Text fz="xs" c="var(--ink-3)">
            {summarize(profile.prefs, renderLabel)}
          </Text>
        </div>
        <Group gap={4} wrap="nowrap">
          <Tooltip label={t`Delete profile`} withArrow>
            <ActionIcon
              variant="subtle"
              color="var(--danger)"
              loading={remove.isPending}
              onClick={() => setConfirming(true)}
              aria-label={t`Delete profile`}
            >
              <IconTrash size={16} />
            </ActionIcon>
          </Tooltip>
          <ActionIcon
            variant="subtle"
            color="var(--neutral)"
            onClick={() => setOpen((value) => !value)}
            aria-label={open ? t`Collapse` : t`Edit profile`}
          >
            {open ? <IconChevronUp size={16} /> : <IconChevronDown size={16} />}
          </ActionIcon>
        </Group>
      </Group>

      {open && (
        <ProfileEditor
          // Remounted on open rather than kept alive hidden, so reopening after a cancel starts
          // from the saved profile instead of the abandoned edit.
          initial={profile}
          // Types claimed elsewhere are removed from the picker so a save can't fail on a clash
          // the user had no way to see.
          taken={all.filter((p) => p.id !== profile.id).flatMap((p) => p.seriesTypes)}
          submitLabel={t`Save`}
          busy={update.isPending}
          onCancel={() => setOpen(false)}
          onSubmit={(input) =>
            update.mutate(
              { id: profile.id, ...input },
              { onSuccess: () => notifications.show({ message: now`Saved`, color: 'var(--ok)' }) },
            )
          }
        />
      )}

      <ConfirmDialog
        opened={confirming}
        onClose={() => setConfirming(false)}
        title={<Trans>Delete {name}?</Trans>}
        confirmLabel={<Trans>Delete profile</Trans>}
        loading={remove.isPending}
        onConfirm={() =>
          remove.mutate(profile.id, {
            onSuccess: () => {
              setConfirming(false)
              notifications.show({ message: now`Deleted "${name}"`, color: 'var(--ok)' })
            },
          })
        }
      >
        <Trans>The profile and its reader settings are removed. This can't be undone.</Trans>
      </ConfirmDialog>
    </Card>
  )
}

function ProfileEditor({
  fixed = false,
  initial,
  taken,
  submitLabel,
  busy,
  onSubmit,
  onCancel,
}: {
  /** The Default row: no name to edit and no series types to claim. */
  fixed?: boolean
  initial: ReadingProfileInput
  /** Series types another profile already covers; offered but disabled. */
  taken: string[]
  submitLabel: string
  busy: boolean
  onSubmit: (input: ReadingProfileInput) => void
  onCancel: () => void
}) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const [name, setName] = useState(initial.name)
  const [types, setTypes] = useState<string[]>(initial.seriesTypes)
  const [prefs, setPrefs] = useState<ReaderPrefs>(initial.prefs)
  const set = (patch: Partial<ReaderPrefs>) => setPrefs((current) => ({ ...current, ...patch }))

  return (
    <Stack gap="sm" mt="sm">
      {!fixed && (
        <>
          <TextInput
            label={t`Name`}
            value={name}
            maxLength={60}
            onChange={(e) => setName(e.currentTarget.value)}
          />

          <MultiSelect
            label={t`Applies automatically to`}
            description={t`Leave empty to use this profile only where you pin it to a series.`}
            value={types}
            onChange={setTypes}
            data={SERIES_TYPES.map((type) => {
              const typeLabel = renderLabel(SERIES_TYPE_LABELS[type])
              return {
                value: type,
                label: taken.includes(type) ? t`${typeLabel} (another profile)` : typeLabel,
                disabled: taken.includes(type),
              }
            })}
          />
        </>
      )}

      <Group grow align="flex-start">
        <Select
          label={t`Layout`}
          allowDeselect={false}
          value={prefs.mode}
          onChange={(value) => value && set({ mode: value as ReaderPrefs['mode'] })}
          data={[
            { value: 'paged', label: t`Single page` },
            { value: 'double', label: t`Two pages side by side` },
            { value: 'vertical', label: t`Continuous vertical (webtoon)` },
          ]}
        />
        <Select
          label={t`Direction`}
          allowDeselect={false}
          value={prefs.direction}
          onChange={(value) => value && set({ direction: value as ReaderPrefs['direction'] })}
          data={[
            { value: 'rtl', label: t`Right to left (manga)` },
            { value: 'ltr', label: t`Left to right` },
          ]}
        />
        <Select
          label={t`Page fit`}
          allowDeselect={false}
          value={prefs.fit}
          onChange={(value) => value && set({ fit: value as ReaderPrefs['fit'] })}
          data={[
            { value: 'height', label: t`Fit height` },
            { value: 'width', label: t`Fit width` },
            { value: 'screen', label: t`Fit screen` },
            { value: 'original', label: t`Original size (1:1)` },
          ]}
        />
        {prefs.fit === 'original' && (
          <NumberInput
            label={t`Scale`}
            suffix="%"
            min={25}
            max={400}
            step={5}
            value={prefs.scale}
            onChange={(value) => set({ scale: Number(value) || 100 })}
          />
        )}
      </Group>

      <Group grow align="flex-start">
        <Select
          label={t`Background`}
          allowDeselect={false}
          value={prefs.background === BACKGROUNDS.oled ? 'oled' : 'dark'}
          onChange={(value) =>
            set({ background: value === 'oled' ? BACKGROUNDS.oled : BACKGROUNDS.dark })
          }
          data={[
            { value: 'dark', label: t`Dark` },
            { value: 'oled', label: t`OLED black` },
          ]}
        />
        <NumberInput
          label={t`Page gap`}
          description={t`Continuous layout only, in pixels.`}
          min={0}
          max={64}
          value={prefs.pageGap}
          onChange={(value) => set({ pageGap: typeof value === 'number' ? value : 0 })}
        />
        <NumberInput
          label={t`Preload`}
          description={t`Pages fetched ahead.`}
          min={0}
          max={10}
          value={prefs.preload}
          onChange={(value) => set({ preload: typeof value === 'number' ? value : 0 })}
        />
      </Group>

      <Switch
        size="sm"
        label={t`Advance to the next chapter at the end`}
        checked={prefs.autoNextChapter}
        onChange={(e) => set({ autoNextChapter: e.currentTarget.checked })}
      />
      <Switch
        size="sm"
        label={t`Tap zones (click the page edges to turn)`}
        checked={prefs.tapZones}
        onChange={(e) => set({ tapZones: e.currentTarget.checked })}
      />
      <Switch
        size="sm"
        label={t`Show page number`}
        checked={prefs.showPageNumber}
        onChange={(e) => set({ showPageNumber: e.currentTarget.checked })}
      />
      <Switch
        size="sm"
        label={t`Flash the chapter name on chapter change`}
        description={t`Credit pages and the next chapter's first pages often look alike, so this marks the turn.`}
        checked={prefs.chapterBanner}
        onChange={(e) => set({ chapterBanner: e.currentTarget.checked })}
      />
      <Switch
        size="sm"
        label={t`Split double-width pages`}
        checked={prefs.splitWidePages}
        onChange={(e) => set({ splitWidePages: e.currentTarget.checked })}
      />

      <Group justify="flex-end" gap="xs">
        <Button size="xs" variant="subtle" color="var(--neutral)" onClick={onCancel}>
          <Trans>Cancel</Trans>
        </Button>
        <Button
          size="xs"
          loading={busy}
          disabled={!fixed && name.trim().length === 0}
          onClick={() => onSubmit({ name: name.trim(), prefs, seriesTypes: types })}
        >
          {submitLabel}
        </Button>
      </Group>
    </Stack>
  )
}
