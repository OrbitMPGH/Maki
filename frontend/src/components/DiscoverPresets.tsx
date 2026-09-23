import { useMemo, useState } from 'react'
import { Trans, useLingui } from '@lingui/react/macro'
import { t as now } from '@lingui/core/macro'
import {
  ActionIcon,
  Button,
  Checkbox,
  Group,
  Menu,
  Modal,
  Stack,
  Text,
  TextInput,
  Tooltip,
} from '@mantine/core'
import { notifications } from '@mantine/notifications'
import {
  IconBookmark,
  IconChevronRight,
  IconDeviceFloppy,
  IconEyeOff,
  IconFilter,
  IconPin,
  IconPinnedOff,
  IconTrash,
} from '@tabler/icons-react'
import {
  useCreateDiscoverPreset,
  useDeleteDiscoverPreset,
  useDiscoverFeed,
  useDiscoverPresets,
  useHiddenContent,
  useSaveHiddenContent,
  useUpdateDiscoverPreset,
  type CatalogueTerm,
  type DiscoverPreset,
  type DiscoverRail,
  type RecommendationFilters,
  type RecommendationItem,
} from '../api/hooks'
import { filtersFromSpec } from './CatalogueFilters'
import { TermPicker } from './CatalogueRules'
import { DiscoverRailRow } from './ui/DiscoverRail'
import { SectionHeader } from './ui/SectionHeader'

/** Key prefix for a rail built from a saved filter, so the "Show more" view can recognize it. */
export const PRESET_RAIL_PREFIX = 'preset:'

/**
 * Saved filters for a catalogue filter panel: load one, save the panel as a new one, pin one as a
 * Discover rail. `current` is read when saving rather than passed as a value, because a panel's
 * `build()` is a fresh object each render.
 */
export function PresetMenu({
  current,
  onLoad,
}: {
  current: () => RecommendationFilters
  onLoad: (filters: RecommendationFilters) => void
}) {
  const { t } = useLingui()
  const { data: presets } = useDiscoverPresets()
  const update = useUpdateDiscoverPreset()
  const remove = useDeleteDiscoverPreset()
  const [saveOpen, setSaveOpen] = useState(false)

  return (
    <>
      <Menu position="bottom-start" withinPortal shadow="md" width={280}>
        <Menu.Target>
          <Button size="xs" variant="default" leftSection={<IconBookmark size={14} />}>
            <Trans>Saved filters</Trans>
          </Button>
        </Menu.Target>
        <Menu.Dropdown>
          {(presets ?? []).length === 0 && (
            <Text size="xs" c="var(--ink-3)" px="sm" py={6}>
              <Trans>Nothing saved yet.</Trans>
            </Text>
          )}
          {(presets ?? []).map((preset) => (
            <Group key={preset.id} gap={2} wrap="nowrap" pr={4}>
              <Menu.Item style={{ flex: 1, minWidth: 0 }} onClick={() => onLoad(filtersFromSpec(preset.spec))}>
                <Text size="sm" truncate>
                  {preset.name}
                </Text>
              </Menu.Item>
              <Tooltip label={preset.pinned ? t`Remove from Discover` : t`Show as a rail on Discover`} withArrow>
                <ActionIcon
                  variant="subtle"
                  color={preset.pinned ? 'var(--brand)' : 'var(--neutral)'}
                  aria-label={preset.pinned ? t`Remove from Discover` : t`Show as a rail on Discover`}
                  onClick={() => update.mutate({ id: preset.id, pinned: !preset.pinned })}
                >
                  {preset.pinned ? <IconPinnedOff size={14} /> : <IconPin size={14} />}
                </ActionIcon>
              </Tooltip>
              <ActionIcon
                variant="subtle"
                color="var(--neutral)"
                aria-label={t`Delete saved filter`}
                onClick={() => remove.mutate(preset.id)}
              >
                <IconTrash size={14} />
              </ActionIcon>
            </Group>
          ))}
          <Menu.Divider />
          <Menu.Item leftSection={<IconDeviceFloppy size={14} />} onClick={() => setSaveOpen(true)}>
            <Trans>Save these filters…</Trans>
          </Menu.Item>
        </Menu.Dropdown>
      </Menu>
      <SavePresetModal opened={saveOpen} onClose={() => setSaveOpen(false)} current={current} />
    </>
  )
}

function SavePresetModal({
  opened,
  onClose,
  current,
}: {
  opened: boolean
  onClose: () => void
  current: () => RecommendationFilters
}) {
  const { t } = useLingui()
  const create = useCreateDiscoverPreset()
  const [name, setName] = useState('')
  const [pinned, setPinned] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const submit = () => {
    const trimmed = name.trim()
    if (!trimmed) {
      setError(t`Give it a name`)
      return
    }
    create.mutate(
      { name: trimmed, spec: current(), pinned },
      {
        onSuccess: () => {
          notifications.show({ color: 'green', message: now`Filter saved` })
          setName('')
          onClose()
        },
        onError: (err) => setError(String(err)),
      },
    )
  }

  return (
    <Modal opened={opened} onClose={onClose} title={t`Save filters`} size="sm">
      <Stack gap="sm">
        <TextInput
          label={t`Name`}
          placeholder={t`School romance, no adult cast`}
          value={name}
          error={error}
          data-autofocus
          onChange={(e) => {
            setName(e.currentTarget.value)
            setError(null)
          }}
          onKeyDown={(e) => {
            if (e.key === 'Enter') submit()
          }}
        />
        <Checkbox
          label={t`Show as a rail on Discover`}
          checked={pinned}
          onChange={(e) => setPinned(e.currentTarget.checked)}
        />
        <Group justify="flex-end">
          <Button variant="subtle" onClick={onClose}>
            <Trans>Cancel</Trans>
          </Button>
          <Button loading={create.isPending} onClick={submit}>
            <Trans>Save</Trans>
          </Button>
        </Group>
      </Stack>
    </Modal>
  )
}

/** Opens the never-show list. Shows how many terms it holds so the list is never invisible. */
export function HiddenContentButton() {
  const { data } = useHiddenContent()
  const [open, setOpen] = useState(false)
  const count = data?.terms?.length ?? 0
  return (
    <>
      <Button
        size="xs"
        variant="subtle"
        color="var(--neutral)"
        leftSection={<IconEyeOff size={14} />}
        onClick={() => setOpen(true)}
      >
        {count > 0 ? <Trans>Hidden everywhere ({count})</Trans> : <Trans>Hidden everywhere</Trans>}
      </Button>
      {open && <HiddenContentModal onClose={() => setOpen(false)} />}
    </>
  )
}

function HiddenContentModal({ onClose }: { onClose: () => void }) {
  const { t } = useLingui()
  const { data } = useHiddenContent()
  const save = useSaveHiddenContent()
  const [terms, setTerms] = useState<CatalogueTerm[]>(() => data?.terms ?? [])

  return (
    <Modal opened onClose={onClose} title={t`Hidden everywhere`} size="lg">
      <Stack gap="sm">
        <Text size="sm" c="var(--ink-3)">
          <Trans>
            Series with any of these never show up on Discover: not in rails, search, recommendations,
            or creator pages. Only you see this list.
          </Trans>
        </Text>
        <TermPicker kinds={['genre', 'tag']} tone="exclude" value={terms} onChange={setTerms} allowHide={false} />
        <Group justify="flex-end">
          <Button variant="subtle" onClick={onClose}>
            <Trans>Cancel</Trans>
          </Button>
          <Button
            loading={save.isPending}
            onClick={() =>
              save.mutate(
                { terms },
                {
                  onSuccess: () => {
                    notifications.show({ color: 'green', message: now`Hidden list saved` })
                    onClose()
                  },
                  onError: (err) => notifications.show({ color: 'red', message: String(err) }),
                },
              )
            }
          >
            <Trans>Save</Trans>
          </Button>
        </Group>
      </Stack>
    </Modal>
  )
}

/** Each pinned saved filter as a Discover rail. */
export function PresetRails({
  seriesIdFor,
  onOpen,
  onShowMore,
}: {
  seriesIdFor: (item: RecommendationItem) => number | null
  onOpen: (item: RecommendationItem) => void
  onShowMore: (rail: DiscoverRail) => void
}) {
  const { data: presets } = useDiscoverPresets()
  const pinned = useMemo(() => (presets ?? []).filter((p) => p.pinned), [presets])
  return (
    <>
      {pinned.map((preset) => (
        <PresetRail
          key={preset.id}
          preset={preset}
          seriesIdFor={seriesIdFor}
          onOpen={onOpen}
          onShowMore={onShowMore}
        />
      ))}
    </>
  )
}

function PresetRail({
  preset,
  seriesIdFor,
  onOpen,
  onShowMore,
}: {
  preset: DiscoverPreset
  seriesIdFor: (item: RecommendationItem) => number | null
  onOpen: (item: RecommendationItem) => void
  onShowMore: (rail: DiscoverRail) => void
}) {
  const filters = useMemo(() => filtersFromSpec(preset.spec), [preset.spec])
  const request = useMemo(() => ({ feed: 'Popular', filters, limit: 40 }), [filters])
  const { data: items } = useDiscoverFeed(request)
  if (!items || items.length === 0) return null

  const rail: DiscoverRail = {
    key: `${PRESET_RAIL_PREFIX}${preset.id}`,
    title: preset.name,
    feed: 'Popular',
    genre: null,
    items,
    filters,
  }
  return (
    <div>
      <SectionHeader
        icon={IconFilter}
        title={preset.name}
        count={items.length}
        action={
          <Button
            variant="subtle"
            size="xs"
            rightSection={<IconChevronRight size={14} />}
            onClick={() => onShowMore(rail)}
          >
            <Trans>Show more</Trans>
          </Button>
        }
      />
      <DiscoverRailRow items={items} seriesIdFor={seriesIdFor} onOpen={onOpen} />
    </div>
  )
}
