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

const MODE_LABELS: Record<ReaderPrefs['mode'], string> = {
  paged: 'Single page',
  double: 'Two pages',
  vertical: 'Continuous',
}

const DIRECTION_LABELS: Record<ReaderPrefs['direction'], string> = {
  ltr: 'left to right',
  rtl: 'right to left',
}

const FIT_LABELS: Record<ReaderPrefs['fit'], string> = {
  width: 'fit width',
  height: 'fit height',
  screen: 'fit screen',
  original: '1:1',
}

function summarize(prefs: ReaderPrefs): string {
  return `${MODE_LABELS[prefs.mode]}, ${DIRECTION_LABELS[prefs.direction]}, ${FIT_LABELS[prefs.fit]}`
}

/**
 * A user's named reader presets, and which series types each one is picked for automatically.
 * <p>
 * The type claim is the whole point: a manhwa opens as a continuous left-to-right strip and a manga
 * stays single-page right-to-left with nothing configured per series. A type belongs to at most one
 * profile, so the server refuses a second claimant rather than silently picking one.
 */
export function ReadingProfilesSection() {
  const { data: profiles } = useReadingProfiles()
  const create = useCreateReadingProfile()
  const [creating, setCreating] = useState(false)

  return (
    <Card withBorder radius="md" padding="md">
      <Group justify="space-between" mb="sm">
        <Title order={4}>Reading profiles</Title>
        <Button
          size="xs"
          variant="light"
          leftSection={<IconPlus size={14} />}
          onClick={() => setCreating((open) => !open)}
        >
          New profile
        </Button>
      </Group>

      <Text size="sm" c="dimmed" mb="md">
        Named reader settings, picked automatically from a series' type. Series with a type no
        profile covers fall back to the Reader defaults above. A series whose metadata hasn't been
        refreshed since upgrading has no type yet, so it does the same until the next metadata run.
        You can still pin a profile, or override the settings outright, from inside the reader.
      </Text>

      {creating && (
        <ProfileEditor
          key="new"
          initial={{ name: '', prefs: DEFAULT_PREFS, seriesTypes: [] }}
          taken={(profiles ?? []).flatMap((p) => p.seriesTypes)}
          submitLabel="Create"
          busy={create.isPending}
          onCancel={() => setCreating(false)}
          onSubmit={(input) =>
            create.mutate(input, {
              onSuccess: () => {
                setCreating(false)
                notifications.show({ message: 'Profile created', color: 'green' })
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
            No profiles. Every series uses the reader defaults.
          </Text>
        )}
      </Stack>
    </Card>
  )
}

function ProfileRow({ profile, all }: { profile: ReadingProfile; all: ReadingProfile[] }) {
  const [open, setOpen] = useState(false)
  const update = useUpdateReadingProfile()
  const remove = useDeleteReadingProfile()

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
                {SERIES_TYPE_LABELS[type] ?? type}
              </Badge>
            ))}
          </Group>
          <Text fz="xs" c="dimmed">
            {summarize(profile.prefs)}
          </Text>
        </div>
        <Group gap={4} wrap="nowrap">
          <Tooltip label="Delete profile" withArrow>
            <ActionIcon
              variant="subtle"
              color="red"
              loading={remove.isPending}
              onClick={() =>
                remove.mutate(profile.id, {
                  onSuccess: () =>
                    notifications.show({ message: `Deleted "${profile.name}"`, color: 'green' }),
                })
              }
              aria-label="Delete profile"
            >
              <IconTrash size={16} />
            </ActionIcon>
          </Tooltip>
          <ActionIcon
            variant="subtle"
            color="gray"
            onClick={() => setOpen((value) => !value)}
            aria-label={open ? 'Collapse' : 'Edit profile'}
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
          submitLabel="Save"
          busy={update.isPending}
          onCancel={() => setOpen(false)}
          onSubmit={(input) =>
            update.mutate(
              { id: profile.id, ...input },
              { onSuccess: () => notifications.show({ message: 'Saved', color: 'green' }) },
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
  const [name, setName] = useState(initial.name)
  const [types, setTypes] = useState<string[]>(initial.seriesTypes)
  const [prefs, setPrefs] = useState<ReaderPrefs>(initial.prefs)
  const set = (patch: Partial<ReaderPrefs>) => setPrefs((current) => ({ ...current, ...patch }))

  return (
    <Stack gap="sm" mt="sm">
      <TextInput
        label="Name"
        value={name}
        maxLength={60}
        onChange={(e) => setName(e.currentTarget.value)}
      />

      <MultiSelect
        label="Applies automatically to"
        description="Leave empty to use this profile only where you pin it to a series."
        value={types}
        onChange={setTypes}
        data={SERIES_TYPES.map((type) => ({
          value: type,
          label: taken.includes(type)
            ? `${SERIES_TYPE_LABELS[type]} (another profile)`
            : SERIES_TYPE_LABELS[type],
          disabled: taken.includes(type),
        }))}
      />

      <Group grow align="flex-start">
        <Select
          label="Layout"
          allowDeselect={false}
          value={prefs.mode}
          onChange={(value) => value && set({ mode: value as ReaderPrefs['mode'] })}
          data={[
            { value: 'paged', label: 'Single page' },
            { value: 'double', label: 'Two pages side by side' },
            { value: 'vertical', label: 'Continuous vertical (webtoon)' },
          ]}
        />
        <Select
          label="Direction"
          allowDeselect={false}
          value={prefs.direction}
          onChange={(value) => value && set({ direction: value as ReaderPrefs['direction'] })}
          data={[
            { value: 'rtl', label: 'Right to left (manga)' },
            { value: 'ltr', label: 'Left to right' },
          ]}
        />
        <Select
          label="Page fit"
          allowDeselect={false}
          value={prefs.fit}
          onChange={(value) => value && set({ fit: value as ReaderPrefs['fit'] })}
          data={[
            { value: 'height', label: 'Fit height' },
            { value: 'width', label: 'Fit width' },
            { value: 'screen', label: 'Fit screen' },
            { value: 'original', label: 'Original size (1:1)' },
          ]}
        />
        {prefs.fit === 'original' && (
          <NumberInput
            label="Scale"
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
          label="Background"
          allowDeselect={false}
          value={prefs.background === BACKGROUNDS.oled ? 'oled' : 'dark'}
          onChange={(value) =>
            set({ background: value === 'oled' ? BACKGROUNDS.oled : BACKGROUNDS.dark })
          }
          data={[
            { value: 'dark', label: 'Dark' },
            { value: 'oled', label: 'OLED black' },
          ]}
        />
        <NumberInput
          label="Page gap"
          description="Continuous layout only, in pixels."
          min={0}
          max={64}
          value={prefs.pageGap}
          onChange={(value) => set({ pageGap: typeof value === 'number' ? value : 0 })}
        />
        <NumberInput
          label="Preload"
          description="Pages fetched ahead."
          min={0}
          max={10}
          value={prefs.preload}
          onChange={(value) => set({ preload: typeof value === 'number' ? value : 0 })}
        />
      </Group>

      <Switch
        size="sm"
        label="Advance to the next chapter at the end"
        checked={prefs.autoNextChapter}
        onChange={(e) => set({ autoNextChapter: e.currentTarget.checked })}
      />
      <Switch
        size="sm"
        label="Tap zones (click the page edges to turn)"
        checked={prefs.tapZones}
        onChange={(e) => set({ tapZones: e.currentTarget.checked })}
      />
      <Switch
        size="sm"
        label="Show page number"
        checked={prefs.showPageNumber}
        onChange={(e) => set({ showPageNumber: e.currentTarget.checked })}
      />
      <Switch
        size="sm"
        label="Split double-width pages"
        checked={prefs.splitWidePages}
        onChange={(e) => set({ splitWidePages: e.currentTarget.checked })}
      />

      <Group justify="flex-end" gap="xs">
        <Button size="xs" variant="subtle" color="gray" onClick={onCancel}>
          Cancel
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
