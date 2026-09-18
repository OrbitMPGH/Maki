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
 * A user's named reader presets, and which series types each one is picked for automatically.
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
    <Card withBorder radius="md" padding="md">
      <Group justify="space-between" mb="sm">
        <Title order={4}>
          <Trans>Reading profiles</Trans>
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

      <Text size="sm" c="dimmed" mb="md">
        <Trans>
          Named reader settings, picked automatically from a series' type. Series with a type no
          profile covers fall back to the Reader defaults above. A series whose metadata hasn't been
          refreshed since upgrading has no type yet, so it does the same until the next metadata run.
          You can still pin a profile, or override the settings outright, from inside the reader.
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
                notifications.show({ message: now`Profile created`, color: 'green' })
              },
            })
          }
        />
      )}

      <Stack gap="xs" mt={creating ? 'md' : undefined}>
        {(profiles ?? []).map((profile) => (
          <ProfileRow key={profile.id} profile={profile} all={profiles ?? []} />
        ))}
        {profiles?.length === 0 && !creating && (
          <Text size="sm" c="dimmed">
            <Trans>No profiles. Every series uses the reader defaults.</Trans>
          </Text>
        )}
      </Stack>
    </Card>
  )
}

function ProfileRow({ profile, all }: { profile: ReadingProfile; all: ReadingProfile[] }) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const [open, setOpen] = useState(false)
  const update = useUpdateReadingProfile()
  const remove = useDeleteReadingProfile()
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
          <Text fz="xs" c="dimmed">
            {summarize(profile.prefs, renderLabel)}
          </Text>
        </div>
        <Group gap={4} wrap="nowrap">
          <Tooltip label={t`Delete profile`} withArrow>
            <ActionIcon
              variant="subtle"
              color="red"
              loading={remove.isPending}
              onClick={() =>
                remove.mutate(profile.id, {
                  onSuccess: () =>
                    notifications.show({ message: now`Deleted "${name}"`, color: 'green' }),
                })
              }
              aria-label={t`Delete profile`}
            >
              <IconTrash size={16} />
            </ActionIcon>
          </Tooltip>
          <ActionIcon
            variant="subtle"
            color="gray"
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
              { onSuccess: () => notifications.show({ message: now`Saved`, color: 'green' }) },
            )
          }
        />
      )}
    </Card>
  )
}

function ProfileEditor({
  initial,
  taken,
  submitLabel,
  busy,
  onSubmit,
  onCancel,
}: {
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
        <Button size="xs" variant="subtle" color="gray" onClick={onCancel}>
          <Trans>Cancel</Trans>
        </Button>
        <Button
          size="xs"
          loading={busy}
          disabled={name.trim().length === 0}
          onClick={() => onSubmit({ name: name.trim(), prefs, seriesTypes: types })}
        >
          {submitLabel}
        </Button>
      </Group>
    </Stack>
  )
}
