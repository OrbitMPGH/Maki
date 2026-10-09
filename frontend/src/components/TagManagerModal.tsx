import { useState } from 'react'
import {
  ActionIcon,
  Badge,
  Button,
  ColorSwatch,
  Group,
  Modal,
  Popover,
  Stack,
  Text,
  TextInput,
} from '@mantine/core'
import { IconCheck, IconPencil, IconPlus, IconTrash, IconX } from '@tabler/icons-react'
import { Trans, Plural, useLingui } from '@lingui/react/macro'
import { useCreateTag, useDeleteTag, useTags, useUpdateTag } from '../api/hooks'
import { ConfirmDialog } from './ui/ConfirmDialog'

const COLORS = ['blue', 'grape', 'teal', 'orange', 'violet', 'cyan', 'pink', 'lime', 'indigo', 'red', 'gray']

/** Library-wide tag admin: create, rename, recolour, delete. Deleting unlinks it everywhere. */
export function TagManagerModal({ opened, onClose }: { opened: boolean; onClose: () => void }) {
  const { t } = useLingui()
  const { data: tags } = useTags()
  const createTag = useCreateTag()
  const updateTag = useUpdateTag()
  const deleteTag = useDeleteTag()

  const [newLabel, setNewLabel] = useState('')
  const [editingId, setEditingId] = useState<number | null>(null)
  const [editLabel, setEditLabel] = useState('')
  const [deleting, setDeleting] = useState<{ id: number; label: string; seriesCount: number } | null>(null)
  const deletingSeriesCount = deleting?.seriesCount ?? 0

  const create = () => {
    const label = newLabel.trim()
    if (!label) return
    createTag.mutate({ label }, { onSuccess: () => setNewLabel('') })
  }

  const saveLabel = (id: number) => {
    const label = editLabel.trim()
    if (!label) return setEditingId(null)
    updateTag.mutate({ id, label }, { onSuccess: () => setEditingId(null) })
  }

  return (
    <Modal opened={opened} onClose={onClose} title={t`Manage tags`} size="md">
      <Stack gap="sm">
        <Group gap="xs">
          <TextInput
            aria-label={t`New tag…`}
            placeholder={t`New tag…`}
            value={newLabel}
            onChange={(e) => setNewLabel(e.currentTarget.value)}
            onKeyDown={(e) => e.key === 'Enter' && create()}
            style={{ flex: 1 }}
          />
          <Button
            leftSection={<IconPlus size={15} />}
            onClick={create}
            loading={createTag.isPending}
            disabled={!newLabel.trim()}
          >
            <Trans>Add</Trans>
          </Button>
        </Group>

        {(tags ?? []).length === 0 && (
          <Text size="sm" c="var(--ink-3)">
            <Trans>No tags yet.</Trans> <Trans>Add one above, or tag a series from its detail page.</Trans>
          </Text>
        )}

        {(tags ?? []).map((tag) => {
          const { id, label, color, seriesCount } = tag
          return (
            <Group key={id} gap="xs" wrap="nowrap">
              {editingId === id ? (
                <>
                  <TextInput
                    aria-label={t`Rename ${label}`}
                    value={editLabel}
                    onChange={(e) => setEditLabel(e.currentTarget.value)}
                    onKeyDown={(e) => e.key === 'Enter' && saveLabel(id)}
                    size="xs"
                    style={{ flex: 1 }}
                    autoFocus
                  />
                  <ActionIcon variant="subtle" color="var(--ok)" onClick={() => saveLabel(id)} aria-label={t`Save`}>
                    <IconCheck size={15} />
                  </ActionIcon>
                  <ActionIcon variant="subtle" color="var(--neutral)" onClick={() => setEditingId(null)} aria-label={t`Cancel`}>
                    <IconX size={15} />
                  </ActionIcon>
                </>
              ) : (
                <>
                  <Popover position="bottom-start" withArrow>
                    <Popover.Target>
                      <Badge color={color} variant="light" style={{ cursor: 'pointer' }}>
                        {label}
                      </Badge>
                    </Popover.Target>
                    <Popover.Dropdown p="xs">
                      <Group gap={6} maw={200}>
                        {COLORS.map((c) => (
                          <ColorSwatch
                            key={c}
                            component="button"
                            color={`var(--mantine-color-${c}-6)`}
                            size={20}
                            style={{ cursor: 'pointer' }}
                            onClick={() => updateTag.mutate({ id, color: c })}
                          />
                        ))}
                      </Group>
                    </Popover.Dropdown>
                  </Popover>
                  <Text size="xs" c="var(--ink-3)" style={{ flex: 1 }} className="tnum">
                    <Plural value={seriesCount} one="# series" other="# series" />
                  </Text>
                  <ActionIcon
                    variant="subtle"
                    color="var(--neutral)"
                    onClick={() => {
                      setEditingId(id)
                      setEditLabel(label)
                    }}
                    aria-label={t`Rename ${label}`}
                  >
                    <IconPencil size={15} />
                  </ActionIcon>
                  <ActionIcon
                    variant="subtle"
                    color="var(--danger)"
                    onClick={() => setDeleting({ id, label, seriesCount })}
                    aria-label={t`Delete ${label}`}
                  >
                    <IconTrash size={15} />
                  </ActionIcon>
                </>
              )}
            </Group>
          )
        })}
      </Stack>

      <ConfirmDialog
        opened={deleting !== null}
        onClose={() => setDeleting(null)}
        title={<Trans>Delete this tag?</Trans>}
        confirmLabel={<Trans>Delete tag</Trans>}
        loading={deleteTag.isPending}
        onConfirm={() => deleting && deleteTag.mutate(deleting.id, { onSuccess: () => setDeleting(null) })}
      >
        <Stack gap="xs">
          <Text size="sm" fw={600}>{deleting?.label}</Text>
          <Text size="sm">
            <Plural
              value={deletingSeriesCount}
              one="It is removed from # series. This cannot be undone."
              other="It is removed from # series. This cannot be undone."
            />
          </Text>
        </Stack>
      </ConfirmDialog>
    </Modal>
  )
}
