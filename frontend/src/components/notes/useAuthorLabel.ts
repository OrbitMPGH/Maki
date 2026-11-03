import { useLingui } from '@lingui/react/macro'
import type { NoteDto } from '../../api/notes'

/** Who to name as a note's author: yourself, a reader without a display name, or a deleted account. */
export function useAuthorLabel() {
  const { t } = useLingui()
  return (note: NoteDto) => {
    if (note.isMine) return t`You`
    if (note.author === null) return t`Deleted account`
    return note.author.name ?? t`Another reader`
  }
}
