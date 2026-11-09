import { useEffect, useMemo, useRef, useState } from 'react'
import { Button, Drawer, Group, SegmentedControl, Skeleton, Stack, Text } from '@mantine/core'
import { IconCloudOff } from '../../lib/icons'
import { Trans, useLingui } from '@lingui/react/macro'
import { errorText } from '../../api/errorText'
import { notePage } from '../../api/notes'
import NoteComposer, { type ComposerValues } from './NoteComposer'
import NoteThread from './NoteThread'
import { usePhoneSheet } from './phone'
import { useNotesWorkspace } from './useNotesWorkspace'

type Scope = 'page' | 'chapter'

/**
 * Every thread on one chapter and the box to start another, as a drawer. Phones get it as a bottom
 * sheet; wider screens use `NotesDropdown`, the reader's margin and the page tabs instead.
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
      // In the reader, ReaderPage owns Escape so a note being typed is not lost.
      closeOnEscape={!inReader}
      returnFocus
      classNames={{ content: 'notes-drawer notes-surface', body: 'notes-drawer-body', title: 'notes-drawer-title', close: 'notes-close' }}
    >
      <NotesPanelBody
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

export function NotesPanelBody({
  chapterId,
  seriesId,
  page = null,
  pinPage = null,
  pageCount,
  spoilerGuard = false,
  initialThreadId = null,
  onGoToPage,
  takeFocus = false,
  lockPage = false,
}: {
  chapterId: number
  seriesId: number
  page?: number | null
  pinPage?: number | null
  pageCount?: number
  spoilerGuard?: boolean
  initialThreadId?: number | null
  onGoToPage?: (page: number) => void
  takeFocus?: boolean
  /** Only that page's notes, with no way to widen the list: the reader's page tab. */
  lockPage?: boolean
}) {
  const { t } = useLingui()
  const workspace = useNotesWorkspace(chapterId, seriesId)
  const { notes, mutations, threadActions, unavailable, readOnly } = workspace
  const { singleUser, incognito, hasDisplayName, loginName } = workspace.who

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
  const scope: Scope = page === null ? 'chapter' : lockPage ? 'page' : (picked ?? (onPage ? 'page' : 'chapter'))
  const shown = scope === 'page' ? roots.filter((n) => notePage(n, pageCount) === page) : roots

  const scrolledTo = useRef<number | null>(null)
  useEffect(() => {
    if (focusId === null || scrolledTo.current === focusId) return
    const thread = panel.current?.querySelector<HTMLElement>(`#note-thread-${focusId}`)
    if (!thread) return
    scrolledTo.current = focusId
    thread.scrollIntoView({ block: 'center' })
  }, [focusId, roots, scope])

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
      {page !== null && !lockPage && (
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
            <IconCloudOff size={18} color="var(--warn)" aria-hidden />
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
            {scope === 'page' && !lockPage && roots.length > 0 && (
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
