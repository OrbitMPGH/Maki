import { useState } from 'react'
import { Button, TextInput } from '@mantine/core'
import { IconBookmark, IconBookmarkFilled } from '../../lib/icons'
import { Trans, useLingui } from '@lingui/react/macro'
import { useShortlist, useUpdateShortlistNote, type ShortlistSource } from '../../api/shortlist'
import { useShortlistToggle } from './shortlistActions'

/**
 * Keeps a title for later without adding it. Pressed, it reads "Shortlisted" with the filled
 * bookmark; pressing it again removes the title, with an Undo toast.
 */
export function ShortlistButton({
  providerId,
  title,
  source,
}: {
  providerId: string
  title: string
  source?: ShortlistSource
}) {
  const { saved, usable, toggle } = useShortlistToggle(providerId, title, source)
  if (!usable) return null
  return (
    <Button
      className="shortlist-button"
      variant={saved ? 'light' : 'default'}
      fullWidth
      aria-pressed={saved}
      leftSection={saved ? <IconBookmarkFilled size={16} /> : <IconBookmark size={16} />}
      onClick={toggle}
    >
      {saved ? <Trans>Shortlisted</Trans> : <Trans>Shortlist</Trans>}
    </Button>
  )
}

/** The "Note for later" field under the button. Mounted only while the title is on the list. */
export function ShortlistNote({ providerId }: { providerId: string }) {
  const { data: list } = useShortlist()
  const entry = list?.entries.find((e) => String(e.mangaBakaId) === providerId || e.item?.providerId === providerId)
  if (!entry) return null
  // Keyed by the stored note so a change from another tab replaces what was typed here.
  return <NoteField key={entry.note ?? ''} id={entry.mangaBakaId} saved={entry.note ?? ''} />
}

function NoteField({ id, saved }: { id: number; saved: string }) {
  const { t } = useLingui()
  const [value, setValue] = useState(saved)
  const update = useUpdateShortlistNote()
  return (
    <TextInput
      size="xs"
      aria-label={t`Note for later`}
      placeholder={t`Note for later`}
      maxLength={500}
      value={value}
      onChange={(e) => setValue(e.currentTarget.value)}
      onBlur={() => {
        if (value.trim() !== saved.trim()) update.mutate({ id, note: value })
      }}
    />
  )
}
