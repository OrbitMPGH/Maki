import { useMemo, useState, type CSSProperties } from 'react'
import { TagsInput, Title } from '@mantine/core'
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
    // Same chips as the provider tags right above, so the two lists read as one system and the
    // labels are what tells them apart. The dot keeps carrying the colour the user picked.
    return (
      <div>
        <Title order={4} fz={14} mb={10}>
          Your tags
        </Title>
        <div className="tag-chips">
          {assigned.map((t) => (
            <span
              key={t.id}
              className="tag-chip"
              style={{ '--bucket': `var(--mantine-color-${t.color}-6)` } as CSSProperties}
            >
              <i className="tag-dot" />
              <span>{t.label}</span>
            </span>
          ))}
          <button type="button" className="tag-more" onClick={() => setEditing(true)}>
            {assigned.length > 0 ? 'Edit' : '+ Add tags'}
          </button>
        </div>
      </div>
    )
  }

  return (
    <TagsInput
      label="Your tags"
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
