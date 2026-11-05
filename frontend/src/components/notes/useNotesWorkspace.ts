import { isOfflineUnavailable } from '../../api/client'
import { useChapterNotes, useNoteActions, useSeriesIncognito } from '../../api/notes'
import { useAuth } from '../../auth/AuthProvider'
import { useConnectivity } from '../../lib/connectivity'
import type { ComposerValues } from './NoteComposer'
import type { NoteThreadActions } from './NoteThread'

/** One chapter's notes plus everything a list of threads and a composer need around them. */
export function useNotesWorkspace(chapterId: number, seriesId: number) {
  const { me, singleUser } = useAuth()
  const notes = useChapterNotes(chapterId)
  const mutations = useNoteActions(chapterId, seriesId)
  const incognito = useSeriesIncognito(seriesId, !singleUser).data ?? false
  const offline = useConnectivity() === 'offline'
  const unavailable = isOfflineUnavailable(notes.error)
  const hasDisplayName = Boolean(me?.displayName?.trim())

  const threadActions: NoteThreadActions = {
    reply: async (rootId, body) => {
      await mutations.reply.mutateAsync({ rootId, body })
    },
    update: async (id, patch) => {
      await mutations.update.mutateAsync({ id, patch })
    },
    remove: async (id) => {
      await mutations.remove.mutateAsync(id)
    },
  }

  return {
    notes,
    roots: notes.data ?? [],
    mutations,
    threadActions,
    create: (values: ComposerValues) => mutations.create.mutateAsync(values),
    unavailable,
    readOnly: offline || unavailable,
    who: {
      singleUser,
      incognito,
      hasDisplayName,
      loginName: !hasDisplayName && me?.loginNamesVisible ? (me.userName ?? null) : null,
    },
  }
}

export type NotesWorkspace = ReturnType<typeof useNotesWorkspace>
