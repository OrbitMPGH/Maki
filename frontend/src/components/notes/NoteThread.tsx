import { useState } from 'react'
import { Badge, Button, Group, Stack, Text } from '@mantine/core'
import { IconLock, IconUsers } from '../../lib/icons'
import { Trans, useLingui } from '@lingui/react/macro'
import { errorText } from '../../api/errorText'
import { notePage, type NoteDto, type NotePatch } from '../../api/notes'
import { formatDateTime } from '../../format'
import NoteBody from './NoteBody'
import NoteComposer, { type ComposerValues } from './NoteComposer'
import { useAuthorLabel } from './useAuthorLabel'

export interface NoteThreadActions {
  reply: (rootId: number, body: string) => Promise<void>
  update: (id: number, patch: NotePatch) => Promise<void>
  remove: (id: number) => Promise<void>
}

/** A root note and the replies under it, with the author's own edit, delete and reply controls. */
export default function NoteThread({
  note,
  actions,
  fold = false,
  readOnly = false,
  pageCount,
  onGoToPage,
  singleUser = false,
  incognito = false,
  hasDisplayName = true,
  loginName = null,
  highlighted = false,
}: {
  note: NoteDto
  actions: NoteThreadActions
  /** Someone else's text on a chapter you have not finished stays hidden until asked for. */
  fold?: boolean
  /** Offline: nothing can be saved. */
  readOnly?: boolean
  pageCount?: number
  /** Reader only: makes the page chip jump there. */
  onGoToPage?: (page: number) => void
  singleUser?: boolean
  incognito?: boolean
  hasDisplayName?: boolean
  /** The login name others see instead of a display name, when the instance shows them. */
  loginName?: string | null
  highlighted?: boolean
}) {
  const { t } = useLingui()
  const authorLabel = useAuthorLabel()
  const [shown, setShown] = useState(false)
  const [editing, setEditing] = useState<number | null>(null)
  const [replying, setReplying] = useState(false)
  const [deleting, setDeleting] = useState<number | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const replies = note.replies ?? []
  const folded = fold && !note.isMine && note.visibility === 'shared' && !shown

  const run = async (work: () => Promise<void>, done: () => void) => {
    setBusy(true)
    setError(null)
    try {
      await work()
      done()
    } catch (e) {
      setError(errorText(e))
      throw e
    } finally {
      setBusy(false)
    }
  }

  if (folded) {
    const who = authorLabel(note)
    const when = formatDateTime(note.createdAt)
    return (
      <article id={`note-thread-${note.id}`} className="note-thread" data-highlight={highlighted || undefined}>
        <button type="button" className="note-fold" onClick={() => setShown(true)}>
          <span>{t`${who}, ${when}`}</span>
          <span className="note-fold-action">
            <Trans>Tap to show</Trans>
          </span>
        </button>
      </article>
    )
  }

  const saveEdit = (item: NoteDto) => (values: ComposerValues) =>
    run(
      async () => {
        const patch: NotePatch = {}
        if (values.body !== item.body) patch.body = values.body
        if (item.parentId === null) {
          if (values.visibility !== item.visibility) patch.visibility = values.visibility
          if (values.pageIndex !== item.pageIndex) {
            if (values.pageIndex === null) patch.clearPage = true
            else patch.pageIndex = values.pageIndex
          }
        }
        if (Object.keys(patch).length > 0) await actions.update(item.id, patch)
      },
      () => setEditing(null),
    )

  const item = (n: NoteDto) => {
    const isRoot = n.parentId === null
    const page = isRoot ? notePage(n, pageCount) : null
    const when = formatDateTime(n.createdAt)
    const author = authorLabel(n)
    const pageNumber = page === null ? 0 : page + 1
    return (
      <div key={n.id} className="note-item" data-reply={!isRoot || undefined}>
        <Group gap={6} wrap="wrap" className="note-head">
          <Text size="sm" fw={600} className="note-author">
            {author}
          </Text>
          {isRoot && n.isMine && !singleUser && n.deleted === null && (
            <Badge
              size="xs"
              variant="light"
              color="var(--neutral)"
              leftSection={n.visibility === 'shared' ? <IconUsers size={10} /> : <IconLock size={10} />}
            >
              {n.visibility === 'shared' ? <Trans>Shared</Trans> : <Trans>Private</Trans>}
            </Badge>
          )}
          {page !== null &&
            (onGoToPage ? (
              <Badge
                size="xs"
                variant="outline"
                color="var(--neutral)"
                component="button"
                type="button"
                className="note-page-chip"
                aria-label={t`Go to page ${pageNumber}`}
                onClick={() => onGoToPage(page)}
              >
                <Trans>Page {pageNumber}</Trans>
              </Badge>
            ) : (
              <Badge size="xs" variant="outline" color="var(--neutral)">
                <Trans>Page {pageNumber}</Trans>
              </Badge>
            ))}
          <Text size="xs" c="var(--ink-3)" component="time" dateTime={n.createdAt}>
            {when}
          </Text>
          {n.editedAt !== null && n.deleted === null && (
            <Text size="xs" c="var(--ink-3)">
              <Trans>edited</Trans>
            </Text>
          )}
        </Group>

        {n.deleted !== null ? (
          <Text size="sm" c="var(--ink-3)" fs="italic">
            {n.deleted === 'moderator' ? <Trans>Removed by an admin</Trans> : <Trans>Note deleted</Trans>}
          </Text>
        ) : editing === n.id ? (
          <NoteComposer
            variant={isRoot ? 'note' : 'reply'}
            initial={{ body: n.body, pageIndex: n.pageIndex, visibility: n.visibility }}
            pinPage={null}
            singleUser={singleUser}
            incognito={incognito}
            hasDisplayName={hasDisplayName}
            loginName={loginName}
            pending={busy}
            disabled={readOnly}
            error={error}
            submitLabel={t`Save`}
            onSubmit={saveEdit(n)}
            onCancel={() => {
              setEditing(null)
              setError(null)
            }}
          />
        ) : (
          <NoteBody text={n.body} />
        )}

        {editing !== n.id && deleting === n.id && (
          <Group gap="xs" className="note-actions">
            <Text size="xs" c="var(--danger-fg)">
              <Trans>Delete this note?</Trans>
            </Text>
            <Button
              size="compact-xs"
              color="var(--danger-fill)"
              loading={busy}
              onClick={() =>
                void run(
                  () => actions.remove(n.id),
                  () => setDeleting(null),
                ).catch(() => {})
              }
            >
              <Trans>Delete</Trans>
            </Button>
            <Button size="compact-xs" variant="default" disabled={busy} onClick={() => setDeleting(null)}>
              <Trans>Cancel</Trans>
            </Button>
          </Group>
        )}

        {editing !== n.id && deleting !== n.id && (n.canEdit || n.canDelete) && (
          <Group gap={4} className="note-actions">
            {n.canEdit && (
              <Button
                size="compact-xs"
                variant="subtle"
                color="gray"
                disabled={readOnly}
                onClick={() => {
                  setError(null)
                  setEditing(n.id)
                }}
              >
                <Trans>Edit</Trans>
              </Button>
            )}
            {n.canDelete && (
              <Button
                size="compact-xs"
                variant="subtle"
                color="gray"
                disabled={readOnly}
                onClick={() => {
                  setError(null)
                  setDeleting(n.id)
                }}
              >
                <Trans>Delete</Trans>
              </Button>
            )}
          </Group>
        )}
      </div>
    )
  }

  const canReply = note.visibility === 'shared' && note.deleted === null && !singleUser

  return (
    <article id={`note-thread-${note.id}`} className="note-thread" data-highlight={highlighted || undefined}>
      <Stack gap="xs">
        {item(note)}
        {replies.length > 0 && <div className="note-replies">{replies.map(item)}</div>}
        {error !== null && editing === null && (
          <Text size="xs" c="var(--danger-fg)" role="alert">
            {error}
          </Text>
        )}
        {canReply &&
          (replying ? (
            <NoteComposer
              variant="reply"
              pending={busy}
              disabled={readOnly}
              onSubmit={(values) =>
                run(
                  () => actions.reply(note.id, values.body),
                  () => setReplying(false),
                )
              }
              onCancel={() => setReplying(false)}
            />
          ) : (
            <div>
              <Button size="compact-xs" variant="subtle" color="gray" disabled={readOnly} onClick={() => setReplying(true)}>
                <Trans>Reply</Trans>
              </Button>
            </div>
          ))}
      </Stack>
    </article>
  )
}
