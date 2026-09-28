import { useEffect, useRef, useState } from 'react'
import { Trans, useLingui } from '@lingui/react/macro'
import { t as now } from '@lingui/core/macro'
import {
  ActionIcon,
  Button,
  Group,
  Menu,
  Modal,
  Stack,
  Text,
  TextInput,
} from '@mantine/core'
import { notifications } from '@mantine/notifications'
import {
  IconBookmark,
  IconDeviceFloppy,
  IconEyeOff,
  IconLayoutRows,
  IconTrash,
} from '@tabler/icons-react'
import {
  useCreateDiscoverPreset,
  useDeleteDiscoverPreset,
  useDiscoverPresets,
  useHiddenContent,
  useSaveHiddenContent,
  type CatalogueTerm,
  type RecommendationFilters,
} from '../api/hooks'
import type { CustomRailSpec } from '../api/customRails'
import { filtersFromSpec } from './CatalogueFilters'
import { TermPicker } from './CatalogueRules'
import { CustomRailEditor } from './rails/CustomRailEditor'

/**
 * Saved filters for a catalogue filter panel: load one, save the panel as a new one, or turn the
 * panel into a custom rail. `current` is read when saving rather than passed as a value, because a
 * panel's `build()` is a fresh object each render.
 *
 * @param railDraft What "Save as a rail" starts from. Defaults to a Discover catalogue rail over
 *   `current()`; the Recommended panel passes a recommendation rail with its seeds and dials.
 */
export function PresetMenu({
  current,
  onLoad,
  railDraft,
}: {
  current: () => RecommendationFilters
  onLoad: (filters: RecommendationFilters) => void
  railDraft?: () => CustomRailSpec
}) {
  const { t } = useLingui()
  const { data: presets } = useDiscoverPresets()
  const remove = useDeleteDiscoverPreset()
  const [saveOpen, setSaveOpen] = useState(false)
  const [railSpec, setRailSpec] = useState<CustomRailSpec | null>(null)

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
          <Menu.Item
            leftSection={<IconLayoutRows size={14} />}
            onClick={() => setRailSpec(railDraft ? railDraft() : { source: 'catalogue', filters: current() })}
          >
            <Trans>Save as a rail…</Trans>
          </Menu.Item>
        </Menu.Dropdown>
      </Menu>
      <SavePresetModal opened={saveOpen} onClose={() => setSaveOpen(false)} current={current} />
      {railSpec && (
        <CustomRailEditor
          draft={{ placement: 'discover', spec: railSpec }}
          onClose={() => setRailSpec(null)}
        />
      )}
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
  const [error, setError] = useState<string | null>(null)

  const submit = () => {
    const trimmed = name.trim()
    if (!trimmed) {
      setError(t`Give it a name`)
      return
    }
    create.mutate(
      { name: trimmed, spec: current() },
      {
        onSuccess: () => {
          notifications.show({ color: 'var(--ok)', message: now`Filter saved` })
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
  const { data, isLoading, isError } = useHiddenContent()
  const save = useSaveHiddenContent()
  const [terms, setTerms] = useState<CatalogueTerm[]>([])
  const dirty = useRef(false)

  // Sync from the query until the user actually edits the list (seeding once on mount raced the fetch).
  useEffect(() => {
    if (!dirty.current && data) setTerms(data.terms ?? [])
  }, [data])

  return (
    <Modal opened onClose={onClose} title={t`Hidden everywhere`} size="lg">
      <Stack gap="sm">
        <Text size="sm" c="var(--ink-3)">
          <Trans>
            Series with any of these never show up on Discover: not in rails, search, recommendations,
            or creator pages. Only you see this list.
          </Trans>
        </Text>
        {isLoading ? (
          <Text size="sm" c="var(--ink-3)">
            <Trans>Loading…</Trans>
          </Text>
        ) : (
          <TermPicker
            kinds={['genre', 'tag']}
            tone="exclude"
            value={terms}
            onChange={(next) => {
              dirty.current = true
              setTerms(next)
            }}
            allowHide={false}
          />
        )}
        <Group justify="flex-end">
          <Button variant="subtle" onClick={onClose}>
            <Trans>Cancel</Trans>
          </Button>
          <Button
            loading={save.isPending}
            disabled={isLoading || isError}
            onClick={() =>
              save.mutate(
                { terms },
                {
                  onSuccess: () => {
                    notifications.show({ color: 'var(--ok)', message: now`Hidden list saved` })
                    onClose()
                  },
                  onError: (err) => notifications.show({ color: 'var(--danger)', message: String(err) }),
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

