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
import { notifications } from '@mantine/notifications'
import { useCreateTag, useDeleteTag, useTags, useUpdateTag } from '../api/hooks'

const COLORS = ['blue', 'grape', 'teal', 'orange', 'violet', 'cyan', 'pink', 'lime', 'indigo', 'red', 'gray']

/** Library-wide tag admin: create, rename, recolour, delete. Deleting unlinks it everywhere. */
export function TagManagerModal({ opened, onClose }: { opened: boolean; onClose: () => void }) {
  const { data: tags } = useTags()
  const createTag = useCreateTag()
  const updateTag = useUpdateTag()
  const deleteTag = useDeleteTag()

  const [newLabel, setNewLabel] = useState('')
  const [editingId, setEditingId] = useState<number | null>(null)
  const [editLabel, setEditLabel] = useState('')

  const fail = (err: unknown) => notifications.show({ color: 'red', message: String(err) })

  const create = () => {
    const label = newLabel.trim()
    if (!label) return
    createTag.mutate({ label }, { onSuccess: () => setNewLabel(''), onError: fail })
  }

  const saveLabel = (id: number) => {
    const label = editLabel.trim()
    if (!label) return setEditingId(null)
    updateTag.mutate({ id, label }, { onSuccess: () => setEditingId(null), onError: fail })
  }

  return (
    <Modal opened={opened} onClose={onClose} title="Manage tags" size="md">
      <Stack gap="sm">
        <Group gap="xs">
          <TextInput
            placeholder="New tag…"
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
            Add
          </Button>
        </Group>

        {(tags ?? []).length === 0 && (
          <Text size="sm" c="dimmed">
            No tags yet. Add one above, or tag a series from its detail page.
          </Text>
        )}

        {(tags ?? []).map((tag) => (
          <Group key={tag.id} gap="xs" wrap="nowrap">
            {editingId === tag.id ? (
              <>
                <TextInput
                  value={editLabel}
                  onChange={(e) => setEditLabel(e.currentTarget.value)}
                  onKeyDown={(e) => e.key === 'Enter' && saveLabel(tag.id)}
                  size="xs"
                  style={{ flex: 1 }}
                  autoFocus
                />
                <ActionIcon variant="subtle" color="green" onClick={() => saveLabel(tag.id)} aria-label="Save">
                  <IconCheck size={15} />
                </ActionIcon>
                <ActionIcon variant="subtle" color="gray" onClick={() => setEditingId(null)} aria-label="Cancel">
                  <IconX size={15} />
                </ActionIcon>
              </>
            ) : (
              <>
                <Popover position="bottom-start" withArrow>
                  <Popover.Target>
                    <Badge color={tag.color} variant="light" style={{ cursor: 'pointer' }}>
                      {tag.label}
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
                          onClick={() => updateTag.mutate({ id: tag.id, color: c }, { onError: fail })}
                        />
                      ))}
                    </Group>
                  </Popover.Dropdown>
                </Popover>
                <Text size="xs" c="dimmed" style={{ flex: 1 }} className="tnum">
                  {tag.seriesCount} series
                </Text>
                <ActionIcon
                  variant="subtle"
                  color="gray"
                  onClick={() => {
                    setEditingId(tag.id)
                    setEditLabel(tag.label)
                  }}
                  aria-label={`Rename ${tag.label}`}
                >
                  <IconPencil size={15} />
                </ActionIcon>
                <ActionIcon
                  variant="subtle"
                  color="red"
                  onClick={() => deleteTag.mutate(tag.id, { onError: fail })}
                  aria-label={`Delete ${tag.label}`}
                >
                  <IconTrash size={15} />
                </ActionIcon>
              </>
            )}
          </Group>
        ))}
      </Stack>
    </Modal>
  )
}
