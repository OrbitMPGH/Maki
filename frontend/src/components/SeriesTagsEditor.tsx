import { useMemo, useState } from 'react'
import { Badge, Group, TagsInput, Text } from '@mantine/core'
import { IconTag } from '@tabler/icons-react'
import { notifications } from '@mantine/notifications'
import { useCreateTag, useSetSeriesTags, useTags } from '../api/hooks'

/**
 * Tag assignment for a single series. Works in labels rather than ids because the input has to
 * create as you type: unknown labels are created first (the create endpoint is idempotent, so a
 * label that already exists just comes back), then the whole set is written in one PUT.
 */
export function SeriesTagsEditor({ seriesId, tagIds }: { seriesId: number; tagIds: number[] }) {
  const { data: tags } = useTags()
  const createTag = useCreateTag()
  const setSeriesTags = useSetSeriesTags()
  const [editing, setEditing] = useState(false)

  const assigned = useMemo(
    () => (tags ?? []).filter((t) => tagIds.includes(t.id)),
    [tags, tagIds],
  )

  const apply = async (labels: string[]) => {
    try {
      const byLabel = new Map((tags ?? []).map((t) => [t.label.toLowerCase(), t]))
      const ids: number[] = []
      for (const raw of labels) {
        const label = raw.trim()
        if (!label) continue
        const existing = byLabel.get(label.toLowerCase())
        ids.push(existing ? existing.id : (await createTag.mutateAsync({ label })).id)
      }
      await setSeriesTags.mutateAsync({ seriesId, tagIds: [...new Set(ids)] })
    } catch (err) {
      notifications.show({ color: 'red', message: `Failed to update tags: ${String(err)}` })
    }
  }

  if (!editing) {
    return (
      <Group gap="xs" align="center">
        <Text size="xs" c="dimmed" fw={600} tt="uppercase" style={{ letterSpacing: '0.05em' }}>
          Tags
        </Text>
        {assigned.map((t) => (
          <Badge key={t.id} color={t.color} variant="light" leftSection={<IconTag size={11} />}>
            {t.label}
          </Badge>
        ))}
        <Badge
          variant="outline"
          color="gray"
          style={{ cursor: 'pointer' }}
          onClick={() => setEditing(true)}
        >
          {assigned.length > 0 ? 'Edit' : '+ Add tags'}
        </Badge>
      </Group>
    )
  }

  return (
    <TagsInput
      label="Tags"
      description="Press Enter to create a new tag"
      data={(tags ?? []).map((t) => t.label)}
      value={assigned.map((t) => t.label)}
      onChange={(labels) => void apply(labels)}
      onBlur={() => setEditing(false)}
      disabled={setSeriesTags.isPending || createTag.isPending}
      clearable
      autoFocus
      maw={480}
    />
  )
}
