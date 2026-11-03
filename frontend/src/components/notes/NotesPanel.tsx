import { useEffect, useMemo, useRef, useState } from 'react'
import { Button, Drawer, Group, SegmentedControl, Skeleton, Stack, Text } from '@mantine/core'
import { IconCloudOff } from '@tabler/icons-react'
import { Trans, useLingui } from '@lingui/react/macro'
import { errorText } from '../../api/errorText'
import { isOfflineUnavailable } from '../../api/client'
import { notePage, useChapterNotes, useNoteActions, useSeriesIncognito } from '../../api/notes'
import { useAuth } from '../../auth/AuthProvider'
import { useConnectivity } from '../../lib/connectivity'
import NoteComposer, { type ComposerValues } from './NoteComposer'
import NoteThread, { type NoteThreadActions } from './NoteThread'
import { usePhoneSheet } from './phone'

type Scope = 'page' | 'chapter'

/**
 * Every thread on one chapter and the box to start another. The reader opens it beside the pages
 * (a bottom sheet on a phone), the series page opens it over the table without leaving it.
 */
export default function NotesPanel({
  opened,
  onClose,
  chapterId,
  seriesId,
  chapterLabel,
  inReader = false,
  page = null,
  pinPage = null,
  pageCount,
  spoilerGuard = false,
  initialThreadId = null,
  onGoToPage,
  zIndex,
}: {
  opened: boolean
  onClose: () => void
  chapterId: number
  seriesId: number
  chapterLabel: string
  /** Inside the reader the panel stays beside the page instead of dimming it. */
  inReader?: boolean
  /** The page on screen, zero based, for the "This page" filter. Null hides the filter. */
  page?: number | null
  /** The page a new note is pinned to by default. Null offers no pin (the end screen, the series page). */
  pinPage?: number | null
  pageCount?: number
  /** Other people's shared notes stay folded: the viewer has not finished this chapter. */
  spoilerGuard?: boolean
  initialThreadId?: number | null
  onGoToPage?: (page: number) => void
  zIndex?: number
}) {
  const { t } = useLingui()
  const phone = usePhoneSheet()
  const modeless = inReader && !phone
  return (
    <Drawer
      opened={opened}
      onClose={onClose}
      position={phone ? 'bottom' : 'right'}
      size={phone ? '80dvh' : 'md'}
      zIndex={zIndex}
      title={t`Notes, ${chapterLabel}`}
      withOverlay={!modeless}
      trapFocus={!modeless}
      lockScroll={!modeless}
      closeOnClickOutside={!modeless}
      returnFocus
      classNames={{ content: 'notes-drawer', body: 'notes-drawer-body', title: 'notes-drawer-title' }}
    >
      <PanelBody
        chapterId={chapterId}
        seriesId={seriesId}
        page={page}
        pinPage={pinPage}
        pageCount={pageCount}
        spoilerGuard={spoilerGuard}
        initialThreadId={initialThreadId}
        onGoToPage={onGoToPage}
        takeFocus={modeless}
      />
    </Drawer>
  )
}

function PanelBody({
  chapterId,
  seriesId,
  page,
  pinPage,
  pageCount,
  spoilerGuard,
  initialThreadId,
  onGoToPage,
  takeFocus,
}: {
  chapterId: number
  seriesId: number
  page: number | null
  pinPage: number | null
  pageCount?: number
  spoilerGuard: boolean
  initialThreadId: number | null
  onGoToPage?: (page: number) => void
  takeFocus: boolean
}) {
  const { t } = useLingui()
  const { me, singleUser } = useAuth()
  const notes = useChapterNotes(chapterId)
  const mutations = useNoteActions(chapterId, seriesId)
  const incognito = useSeriesIncognito(seriesId, !singleUser).data ?? false
  const offline = useConnectivity() === 'offline'
  const unavailable = isOfflineUnavailable(notes.error)
  const readOnly = offline || unavailable
  const hasDisplayName = Boolean(me?.displayName?.trim())
  const loginName = !hasDisplayName && me?.loginNamesVisible ? me.userName : null

  const [picked, setPicked] = useState<Scope | null>(initialThreadId === null ? null : 'chapter')
  const [createError, setCreateError] = useState<string | null>(null)
  const [focusId] = useState(initialThreadId)
  const panel = useRef<HTMLDivElement>(null)

  // A panel opened beside the page has no focus trap to pull focus in, and one that leaves focus on
  // the toolbar button would send the next arrow key to the page.
  useEffect(() => {
    if (takeFocus) panel.current?.focus()
  }, [takeFocus])

  const roots = useMemo(() => notes.data ?? [], [notes.data])
  const onPage = page !== null && roots.some((n) => notePage(n, pageCount) === page)
  const scope: Scope = page === null ? 'chapter' : (picked ?? (onPage ? 'page' : 'chapter'))
  const shown = scope === 'page' ? roots.filter((n) => notePage(n, pageCount) === page) : roots

  const scrolledTo = useRef<number | null>(null)
  useEffect(() => {
    if (focusId === null || scrolledTo.current === focusId) return
    const thread = panel.current?.querySelector<HTMLElement>(`#note-thread-${focusId}`)
    if (!thread) return
    scrolledTo.current = focusId
    thread.scrollIntoView({ block: 'center' })
  }, [focusId, roots, scope])

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

  const create = async (values: ComposerValues) => {
    setCreateError(null)
    try {
      await mutations.create.mutateAsync(values)
    } catch (e) {
      setCreateError(errorText(e))
      throw e
    }
    // A note that is not on this page would otherwise vanish from a list filtered to it.
    if (page !== null) setPicked(values.pageIndex === page ? 'page' : 'chapter')
  }

  return (
    <div className="notes-panel" ref={panel} tabIndex={-1}>
      {page !== null && (
        <SegmentedControl
          size="xs"
          fullWidth
          aria-label={t`Which notes to show`}
          value={scope}
          onChange={(value) => setPicked(value as Scope)}
          data={[
            { label: t`This page`, value: 'page' },
            { label: t`Whole chapter`, value: 'chapter' },
          ]}
        />
      )}

      <div className="notes-list">
        {unavailable && notes.data === undefined ? (
          <Group gap={8} wrap="nowrap" className="notes-empty">
            <IconCloudOff size={18} stroke={1.75} color="var(--warn)" aria-hidden />
            <Text c="var(--ink-2)" fw={600}>
              <Trans>Notes need a connection</Trans>
            </Text>
          </Group>
        ) : notes.isLoading ? (
          <Stack gap="sm">
            <Skeleton height={56} />
            <Skeleton height={56} />
          </Stack>
        ) : notes.isError && notes.data === undefined ? (
          <Stack gap="xs" align="flex-start" className="notes-empty">
            <Text c="var(--ink-3)">
              <Trans>Could not load the notes.</Trans>
            </Text>
            <Button size="xs" variant="light" onClick={() => void notes.refetch()}>
              <Trans>Try again</Trans>
            </Button>
          </Stack>
        ) : shown.length === 0 ? (
          <Stack gap="xs" align="flex-start" className="notes-empty">
            <Text c="var(--ink-3)">
              {scope === 'page' ? (
                <Trans>No notes on this page.</Trans>
              ) : (
                <Trans>No notes on this chapter yet.</Trans>
              )}
            </Text>
            {scope === 'page' && roots.length > 0 && (
              <Button size="xs" variant="subtle" color="gray" onClick={() => setPicked('chapter')}>
                <Trans>Show the whole chapter</Trans>
              </Button>
            )}
          </Stack>
        ) : (
          <Stack gap="sm">
            {shown.map((note) => (
              <NoteThread
                key={note.id}
                note={note}
                actions={threadActions}
                fold={spoilerGuard}
                readOnly={readOnly}
                pageCount={pageCount}
                onGoToPage={onGoToPage}
                singleUser={singleUser}
                incognito={incognito}
                hasDisplayName={hasDisplayName}
                loginName={loginName}
                highlighted={note.id === focusId}
              />
            ))}
          </Stack>
        )}
      </div>

      <div className="notes-compose">
        {readOnly && !(unavailable && notes.data === undefined) && (
          <Text size="xs" c="var(--ink-3)" mb={6}>
            <Trans>Notes need a connection</Trans>
          </Text>
        )}
        <NoteComposer
          pinPage={pinPage}
          singleUser={singleUser}
          incognito={incognito}
          hasDisplayName={hasDisplayName}
          loginName={loginName}
          pending={mutations.create.isPending}
          disabled={readOnly}
          error={createError}
          onSubmit={create}
        />
      </div>
    </div>
  )
}
