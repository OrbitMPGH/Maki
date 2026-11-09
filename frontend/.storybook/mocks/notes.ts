import { http, HttpResponse, type HttpHandler } from 'msw'
import type { NoteCountDto, NoteDto, NotePatch, NoteVisibility } from '../../src/api/notes'
import { adminMe, api, failWith, hoursAgo } from './api'

const me = { id: adminMe.id, name: adminMe.displayName }
const rin = { id: 2, name: 'Rin' }
const nameless = { id: 3, name: null }

/** The reader story chapter, where the manifest fixture says three notes sit on pages 2 and 12 and the chapter. */
export const NOTES_CHAPTER = 1132

interface NoteInit extends Partial<NoteDto> {
  id: number
  body: string
}

export function note(init: NoteInit): NoteDto {
  const author = init.author === undefined ? me : init.author
  const isMine = author !== null && author.id === me.id
  const deleted = init.deleted ?? null
  return {
    chapterId: NOTES_CHAPTER,
    parentId: null,
    pageIndex: null,
    visibility: 'private',
    isMine,
    canEdit: isMine && deleted === null,
    canDelete: isMine && deleted === null,
    createdAt: hoursAgo(2),
    editedAt: null,
    deleted,
    ...init,
    author,
  }
}

const reply = (id: number, parentId: number, init: Partial<NoteDto> & { body: string }): NoteDto =>
  note({ id, parentId, visibility: 'shared', ...init })

/** Oldest first, like the server: a removed note holding a thread, then one of each kind of live note. */
export const readerNotes: NoteDto[] = [
  note({
    id: 7,
    visibility: 'shared',
    body: '',
    author: rin,
    deleted: 'author',
    createdAt: hoursAgo(48),
    replies: [reply(8, 7, { body: 'I wrote this one down before it went.', author: nameless, createdAt: hoursAgo(47) })],
  }),
  note({
    id: 1,
    pageIndex: 1,
    body: 'The tea cups on this page come back in chapter 140.',
    createdAt: hoursAgo(30),
  }),
  note({
    id: 2,
    pageIndex: 11,
    visibility: 'shared',
    author: rin,
    body: 'Best fight so far. ||She was holding back the whole time.|| Fan translation notes: https://example.com/notes.',
    createdAt: hoursAgo(20),
    replies: [
      reply(4, 2, { body: 'Agreed, the framing is great.', createdAt: hoursAgo(18) }),
      reply(5, 2, { body: 'Page 12 hit hard.', author: nameless, createdAt: hoursAgo(10) }),
    ],
  }),
  note({
    id: 3,
    visibility: 'shared',
    author: nameless,
    body: 'The last line reads differently in the two sources.\nWorth comparing before the next chapter.',
    createdAt: hoursAgo(5),
  }),
]

export const noteCounts: NoteCountDto[] = [
  { chapterId: 1020, mine: 1, shared: 2, total: 3 },
  { chapterId: 1030, mine: 0, shared: 1, total: 1 },
  { chapterId: 1050, mine: 2, shared: 0, total: 2 },
]

let nextId = 900

const built = (chapterId: number, body: { body: string; pageIndex: number | null; visibility: NoteVisibility }): NoteDto =>
  note({ id: nextId++, chapterId, ...body, createdAt: hoursAgo(0), replies: body.visibility === 'shared' ? [] : undefined })

/**
 * Answers without remembering: a note posted in a story comes back well formed and the list stays as
 * fixtured. A story that needs the list to change layers {@link statefulNotes} on top.
 */
export const notesHandlers: HttpHandler[] = [
  http.get(api('/chapters/:id/notes'), ({ params }) =>
    HttpResponse.json(Number(params.id) === NOTES_CHAPTER ? readerNotes : ([] satisfies NoteDto[])),
  ),
  http.get(api('/series/:id/notes/counts'), () => HttpResponse.json([] satisfies NoteCountDto[])),
  http.post(api('/chapters/:id/notes'), async ({ request, params }) =>
    HttpResponse.json(built(Number(params.id), (await request.json()) as never)),
  ),
  http.post(api('/notes/:id/replies'), async ({ request, params }) => {
    const { body } = (await request.json()) as { body: string }
    return HttpResponse.json(reply(nextId++, Number(params.id), { body, createdAt: hoursAgo(0) }))
  }),
  http.patch(api('/notes/:id'), async ({ request, params }) => {
    const patch = (await request.json()) as NotePatch
    const found = readerNotes.find((n) => n.id === Number(params.id)) ?? note({ id: Number(params.id), body: '' })
    return HttpResponse.json({ ...found, ...(patch.body !== undefined && { body: patch.body }) })
  }),
  http.delete(api('/notes/:id'), () => new HttpResponse(null, { status: 204 })),
]

/**
 * A notes store that behaves like the server for one chapter: posting, editing, replying and deleting
 * change what the next GET returns. Call it once per story run (from `layerHandlers`), so what a
 * play function writes never reaches the next run.
 */
export function statefulNotes(initial: NoteDto[] = readerNotes, chapterId = NOTES_CHAPTER): HttpHandler[] {
  const live: NoteDto[] = structuredClone(initial)
  let id = 2000

  const find = (noteId: number) => {
    for (const root of live) {
      if (root.id === noteId) return { root, item: root }
      const item = root.replies?.find((r) => r.id === noteId)
      if (item) return { root, item }
    }
    return null
  }

  return [
    http.get(api('/chapters/:id/notes'), ({ params }) =>
      HttpResponse.json(Number(params.id) === chapterId ? live : []),
    ),
    http.post(api('/chapters/:id/notes'), async ({ request }) => {
      const body = (await request.json()) as { body: string; pageIndex: number | null; visibility: NoteVisibility }
      const created = note({
        id: id++,
        chapterId,
        ...body,
        createdAt: hoursAgo(0),
        replies: body.visibility === 'shared' ? [] : undefined,
      })
      live.push(created)
      return HttpResponse.json(created)
    }),
    http.post(api('/notes/:id/replies'), async ({ request, params }) => {
      const found = find(Number(params.id))
      if (!found || found.root.visibility !== 'shared') return failWith(404, 'Note not found')
      if (found.root.deleted) return failWith(409, 'That note was deleted.', 'error.notes.deleted')
      const { body } = (await request.json()) as { body: string }
      const created = reply(id++, found.root.id, { body, createdAt: hoursAgo(0) })
      found.root.replies = [...(found.root.replies ?? []), created]
      return HttpResponse.json(created)
    }),
    http.patch(api('/notes/:id'), async ({ request, params }) => {
      const found = find(Number(params.id))
      if (!found) return failWith(404, 'Note not found')
      const patch = (await request.json()) as NotePatch
      if (patch.visibility === 'private' && found.root.replies?.some((r) => !r.isMine)) {
        return failWith(409, 'Other people replied, so this note has to stay shared.', 'error.notes.hasReplies')
      }
      if (patch.body !== undefined) {
        found.item.body = patch.body
        found.item.editedAt = hoursAgo(0)
      }
      if (patch.visibility) found.item.visibility = patch.visibility
      if (patch.clearPage) found.item.pageIndex = null
      else if (patch.pageIndex !== undefined) found.item.pageIndex = patch.pageIndex
      return HttpResponse.json(found.root)
    }),
    http.delete(api('/notes/:id'), ({ params }) => {
      const noteId = Number(params.id)
      const found = find(noteId)
      if (!found) return failWith(404, 'Note not found')
      if (found.item === found.root) {
        const others = (found.root.replies ?? []).some((r) => !r.isMine)
        if (others) {
          found.root.deleted = 'author'
          found.root.body = ''
          found.root.canEdit = false
          found.root.canDelete = false
        } else {
          live.splice(live.indexOf(found.root), 1)
        }
      } else {
        found.root.replies = (found.root.replies ?? []).filter((r) => r.id !== noteId)
      }
      return new HttpResponse(null, { status: 204 })
    }),
  ]
}
