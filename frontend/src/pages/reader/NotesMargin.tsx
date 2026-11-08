import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type CSSProperties, type MouseEvent } from 'react'
import { createPortal } from 'react-dom'
import { Button, Popover } from '@mantine/core'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { errorText } from '../../api/errorText'
import { notePage, type NoteDto } from '../../api/notes'
import NoteBody from '../../components/notes/NoteBody'
import NoteComposer, { type ComposerValues } from '../../components/notes/NoteComposer'
import { NotesPanelBody } from '../../components/notes/NotesPanel'
import { notesEscapeCloses } from '../../components/notes/notesEscape'
import NoteThread from '../../components/notes/NoteThread'
import { useAuthorLabel } from '../../components/notes/useAuthorLabel'
import { useNotesWorkspace, type NotesWorkspace } from '../../components/notes/useNotesWorkspace'
import { IconChevronRight, IconLock, IconMessages, IconPlus, IconUsers } from '../../lib/icons'
import { OVERLAY_Z } from './ReaderSettings'

/** Space between the page edge and the notes, and between the notes and the screen edge. */
const GAP = 24
const MAX_WIDTH = 360
/** Narrower than this and a card cannot hold a readable line, so the page gets a tab instead. */
const MIN_WIDTH = 240

type PagedGeometry = { kind: 'paged'; gutter: number; x: number; top: number; bottom: number }
type StripGeometry = {
  kind: 'strip'
  gutter: number
  host: HTMLElement
  /** The pages' right edge, measured inside the strip. */
  edge: number
  /** The same edge on screen. */
  x: number
  /** Where each page starts, from the top of the strip. */
  tops: Map<number, number>
}
type Geometry = PagedGeometry | StripGeometry

function shownBox(element: Element): DOMRect | null {
  const style = getComputedStyle(element)
  if (style.display === 'none' || style.visibility === 'hidden' || style.position === 'fixed') return null
  const box = element.getBoundingClientRect()
  return box.width > 0 && box.height > 0 ? box : null
}

function measurePaged(surface: HTMLElement): PagedGeometry | null {
  const host = surface.querySelector('.reader-paged')
  if (!host) return null
  let right = -Infinity
  let top = Infinity
  let bottom = -Infinity
  for (const child of host.children) {
    const box = shownBox(child)
    if (!box) continue
    right = Math.max(right, box.right)
    top = Math.min(top, box.top)
    bottom = Math.max(bottom, box.bottom)
  }
  if (right === -Infinity) return null
  const frame = surface.getBoundingClientRect()
  return {
    kind: 'paged',
    gutter: frame.right - right,
    x: right,
    top: Math.max(top, frame.top),
    bottom: Math.min(bottom, frame.bottom),
  }
}

function measureStrip(surface: HTMLElement, pages: number[]): StripGeometry | null {
  const host = surface.querySelector<HTMLElement>('.reader-continuous')
  if (!host) return null
  const tops = new Map<number, number>()
  let edge = 0
  for (const page of pages) {
    const image = host.querySelector<HTMLElement>(`[data-page="${page}"]`)
    if (!image || image.offsetWidth === 0) continue
    edge = Math.max(edge, image.offsetLeft + image.offsetWidth)
    tops.set(page, image.offsetTop)
  }
  if (tops.size === 0) return null
  const x = host.getBoundingClientRect().left + edge
  return { kind: 'strip', gutter: host.clientWidth - edge, host, edge, x, tops }
}

/** Where the pages are on screen, re-measured as they load, resize and turn. */
function useGeometry(surface: HTMLElement | null, vertical: boolean, stripPages: number[], layoutKey: string) {
  const [geometry, setGeometry] = useState<Geometry | null>(null)
  const pagesKey = stripPages.join(',')

  const measure = useCallback(() => {
    if (!surface) return setGeometry(null)
    setGeometry(vertical ? measureStrip(surface, pagesKey ? pagesKey.split(',').map(Number) : []) : measurePaged(surface))
  }, [surface, vertical, pagesKey])

  useLayoutEffect(() => {
    measure()
  }, [measure, layoutKey])

  useEffect(() => {
    if (!surface) return
    let frame = 0
    const schedule = () => {
      cancelAnimationFrame(frame)
      frame = requestAnimationFrame(measure)
    }
    const resize = new ResizeObserver(schedule)
    resize.observe(surface)
    const host = surface.querySelector(vertical ? '.reader-continuous' : '.reader-paged')
    if (host) resize.observe(host)
    // Images report their size by loading, which neither a resize of the frame nor a re-render sees.
    surface.addEventListener('load', schedule, true)
    return () => {
      cancelAnimationFrame(frame)
      resize.disconnect()
      surface.removeEventListener('load', schedule, true)
    }
  }, [surface, vertical, measure, layoutKey])

  return geometry
}

/** Clicks on the notes must not reach the page underneath as a tap. */
const keepToNotes = (event: MouseEvent) => event.stopPropagation()

function CompactNote({ note, folded, anchored, onOpen }: { note: NoteDto; folded: boolean; anchored: boolean; onOpen: () => void }) {
  const { t } = useLingui()
  const authorLabel = useAuthorLabel()
  const replies = note.replies?.length ?? 0
  const shared = note.visibility === 'shared'
  return (
    <article
      className="notes-margin-card"
      data-anchored={anchored || undefined}
      onClick={(event) => {
        if ((event.target as HTMLElement).closest('a, .note-spoiler, button')) return
        onOpen()
      }}
    >
      <button type="button" className="notes-margin-card-head" aria-expanded={false} onClick={onOpen}>
        <span className="notes-margin-author">{note.deleted ? <Trans>Note deleted</Trans> : authorLabel(note)}</span>
        {shared ? <IconUsers size={12} aria-label={t`Shared`} /> : <IconLock size={12} aria-label={t`Private`} />}
        {folded && (
          <span className="notes-margin-show">
            <Trans>Show</Trans>
          </span>
        )}
      </button>
      {!note.deleted && !folded && (
        <div className="notes-margin-text">
          <NoteBody text={note.body} />
        </div>
      )}
      {replies > 0 && (
        <span className="notes-margin-replies">
          <Plural value={replies} one="# reply" other="# replies" />
        </span>
      )}
    </article>
  )
}

function MarginNote({
  note,
  workspace,
  folded,
  anchored,
  pageCount,
  onGoToPage,
}: {
  note: NoteDto
  workspace: NotesWorkspace
  folded: boolean
  anchored: boolean
  pageCount: number
  onGoToPage: (page: number) => void
}) {
  const [open, setOpen] = useState(false)
  if (!open) return <CompactNote note={note} folded={folded} anchored={anchored} onOpen={() => setOpen(true)} />
  return (
    <div className="notes-margin-open" data-anchored={anchored || undefined}>
      <NoteThread
        note={note}
        actions={workspace.threadActions}
        readOnly={workspace.readOnly}
        pageCount={pageCount}
        onGoToPage={onGoToPage}
        {...workspace.who}
      />
      <Button variant="subtle" color="gray" size="compact-xs" className="notes-margin-less" onClick={() => setOpen(false)}>
        <Trans>Show less</Trans>
      </Button>
    </div>
  )
}

function PageNotes({
  page,
  notes,
  workspace,
  fold,
  pageCount,
  onGoToPage,
  addable = true,
}: {
  page: number
  notes: NoteDto[]
  workspace: NotesWorkspace
  fold: (note: NoteDto) => boolean
  pageCount: number
  onGoToPage: (page: number) => void
  /** Offers its own "+ Note"; the strip has one shared button instead. */
  addable?: boolean
}) {
  const [composing, setComposing] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const pageNumber = page + 1

  const create = async (values: ComposerValues) => {
    setError(null)
    try {
      await workspace.create(values)
    } catch (e) {
      setError(errorText(e))
      throw e
    }
    setComposing(false)
  }

  return (
    <section className="notes-margin-group">
      <div className="notes-margin-head">
        <span>
          <Trans>Page {pageNumber}</Trans>
        </span>
        {addable && !composing && !workspace.readOnly && (
          <button type="button" className="notes-margin-add" onClick={() => setComposing(true)}>
            <IconPlus size={14} aria-hidden />
            <Trans>Note</Trans>
          </button>
        )}
      </div>
      {notes.map((note) => (
        <MarginNote
          key={note.id}
          note={note}
          workspace={workspace}
          folded={fold(note)}
          anchored
          pageCount={pageCount}
          onGoToPage={onGoToPage}
        />
      ))}
      {composing && (
        <div className="notes-margin-compose">
          <NoteComposer
            pinPage={page}
            {...workspace.who}
            pending={workspace.mutations.create.isPending}
            disabled={workspace.readOnly}
            error={error}
            onSubmit={create}
            onCancel={() => setComposing(false)}
          />
        </div>
      )}
    </section>
  )
}

function ChapterNotes({
  notes,
  workspace,
  fold,
  pageCount,
  onGoToPage,
}: {
  notes: NoteDto[]
  workspace: NotesWorkspace
  fold: (note: NoteDto) => boolean
  pageCount: number
  onGoToPage: (page: number) => void
}) {
  const [open, setOpen] = useState(false)
  if (notes.length === 0) return null
  const count = notes.length
  return (
    <section className="notes-margin-group">
      <button type="button" className="notes-margin-section" aria-expanded={open} onClick={() => setOpen((o) => !o)}>
        <span>
          <Plural value={count} one="# note on the whole chapter" other="# notes on the whole chapter" />
        </span>
        <IconChevronRight size={14} aria-hidden style={{ transform: open ? 'rotate(90deg)' : undefined }} />
      </button>
      {open &&
        notes.map((note) => (
          <MarginNote
            key={note.id}
            note={note}
            workspace={workspace}
            folded={fold(note)}
            anchored={false}
            pageCount={pageCount}
            onGoToPage={onGoToPage}
          />
        ))}
    </section>
  )
}

type ChromeLift = 'none' | 'bar' | 'strip'

/**
 * The scrolling strip's one way to write a note: it stays put at the foot of the margin and names
 * the page in view, rather than a button that jumps from page to page as they scroll past.
 */
function StripAdd({
  page,
  workspace,
  style,
  lift,
}: {
  page: number
  workspace: NotesWorkspace
  style: CSSProperties
  lift: ChromeLift
}) {
  // Held while writing, so scrolling on does not move the note to another page.
  const [pinned, setPinned] = useState<number | null>(null)
  const [error, setError] = useState<string | null>(null)
  const pageNumber = page + 1

  if (workspace.readOnly) return null

  const create = async (values: ComposerValues) => {
    setError(null)
    try {
      await workspace.create(values)
    } catch (e) {
      setError(errorText(e))
      throw e
    }
    setPinned(null)
  }

  return (
    <div className="notes-strip-add notes-surface" data-lift={lift} style={style} onClick={keepToNotes}>
      {pinned === null ? (
        <button type="button" className="notes-margin-add" onClick={() => setPinned(page)}>
          <IconPlus size={14} aria-hidden />
          <Trans>Note on page {pageNumber}</Trans>
        </button>
      ) : (
        <div className="notes-margin-compose">
          <NoteComposer
            key={pinned}
            pinPage={pinned}
            {...workspace.who}
            pending={workspace.mutations.create.isPending}
            disabled={workspace.readOnly}
            error={error}
            onSubmit={create}
            onCancel={() => setPinned(null)}
          />
        </div>
      )}
    </div>
  )
}

/** A page with notes but no room beside it: a tab on the screen edge that opens them. */
function PageTab({
  page,
  count,
  chapterId,
  seriesId,
  pageCount,
  onGoToPage,
}: {
  page: number
  count: number
  chapterId: number
  seriesId: number
  pageCount: number
  onGoToPage: (page: number) => void
}) {
  const { t } = useLingui()
  const [opened, setOpened] = useState(false)
  const dropdown = useRef<HTMLDivElement>(null)
  const pageNumber = page + 1

  // Captured ahead of the reader's own Escape, which would otherwise leave the chapter.
  useEffect(() => {
    if (!opened) return
    const onEscape = (event: KeyboardEvent) => {
      if (event.key !== 'Escape' || event.isComposing) return
      event.preventDefault()
      if (notesEscapeCloses(event, dropdown.current)) setOpened(false)
    }
    window.addEventListener('keydown', onEscape, true)
    return () => window.removeEventListener('keydown', onEscape, true)
  }, [opened])

  return (
    <Popover
      opened={opened}
      onChange={setOpened}
      width={400}
      position="left-start"
      offset={8}
      shadow="lg"
      zIndex={OVERLAY_Z}
      closeOnEscape={false}
      returnFocus
    >
      <Popover.Target>
        <button
          type="button"
          className="notes-page-tab"
          aria-label={t`Notes on page ${pageNumber} (${count})`}
          aria-expanded={opened}
          onClick={() => setOpened((o) => !o)}
        >
          <IconMessages size={15} aria-hidden />
          <span className="tnum">{count}</span>
        </button>
      </Popover.Target>
      <Popover.Dropdown ref={dropdown} className="notes-dropdown notes-surface" p={0}>
        <div className="notes-dropdown-head">
          <span className="notes-dropdown-title">
            <Trans>Page {pageNumber}</Trans>
          </span>
        </div>
        <NotesPanelBody
          chapterId={chapterId}
          seriesId={seriesId}
          page={page}
          pinPage={page}
          pageCount={pageCount}
          lockPage
          onGoToPage={(target) => {
            setOpened(false)
            onGoToPage(target)
          }}
        />
      </Popover.Dropdown>
    </Popover>
  )
}

/**
 * A chapter's notes beside the pages, where a reader reads them: the notes on the page in view sit
 * in the margin next to it, in a scrolling strip each page's notes travel with that page. Without
 * room for a margin, the pages that have notes get a tab on the screen edge instead.
 */
export default function NotesMargin({
  surface,
  vertical,
  shownPages,
  page,
  chapterId,
  seriesId,
  pageCount,
  finished,
  zoomed,
  layoutKey,
  lift = 'none',
  onGoToPage,
}: {
  surface: HTMLElement | null
  vertical: boolean
  /** The pages on screen in paged layouts, one or a spread. */
  shownPages: number[]
  /** The page in view. */
  page: number
  chapterId: number
  seriesId: number
  pageCount: number
  /** Read to the end, so other people's notes no longer need folding. */
  finished: boolean
  /** Zoomed past the fit, where the page fills the screen and the margin would sit on top of it. */
  zoomed: boolean
  /** Changes whenever the pages may have moved: a turn, a layout or fit change. */
  layoutKey: string
  /** What of the reader's chrome is up at the foot of the screen, which the strip's add chip clears. */
  lift?: ChromeLift
  onGoToPage: (page: number) => void
}) {
  const workspace = useNotesWorkspace(chapterId, seriesId)
  const { roots } = workspace

  const byPage = useMemo(() => {
    const map = new Map<number, NoteDto[]>()
    for (const note of roots) {
      const at = notePage(note, pageCount)
      if (at === null) continue
      map.set(at, [...(map.get(at) ?? []), note])
    }
    return map
  }, [roots, pageCount])
  const wholeChapter = useMemo(() => roots.filter((note) => notePage(note, pageCount) === null), [roots, pageCount])

  const stripPages = useMemo(() => {
    if (!vertical) return []
    const pages = new Set(byPage.keys())
    pages.add(page)
    if (wholeChapter.length > 0) pages.add(0)
    return [...pages].sort((a, b) => a - b)
  }, [vertical, byPage, page, wholeChapter.length])

  const geometry = useGeometry(surface, vertical, stripPages, layoutKey)
  const width = geometry ? Math.min(MAX_WIDTH, geometry.gutter - 2 * GAP) : 0
  const roomy = geometry !== null && !zoomed && width >= MIN_WIDTH

  const fold = useCallback(
    (note: NoteDto) => !finished && !note.isMine && note.deleted === null && note.visibility === 'shared',
    [finished],
  )

  // Each page's notes start level with the page; when the notes above run past that point, they
  // are pushed down below them instead of overlapping. Set on the DOM directly, so it reruns when
  // a thread opens or closes without another render.
  const column = useRef<HTMLDivElement>(null)
  useLayoutEffect(() => {
    const host = column.current
    if (!host) return
    const groups = [...host.children] as HTMLElement[]
    const place = () => {
      let floor = -Infinity
      for (const group of groups) {
        const top = Math.max(Number(group.dataset.top), floor)
        group.style.top = `${top}px`
        floor = top + group.offsetHeight + 16
      }
    }
    place()
    const resize = new ResizeObserver(place)
    for (const group of groups) resize.observe(group)
    return () => resize.disconnect()
  })

  if (workspace.notes.data === undefined) return null

  if (!roomy) {
    const tabbed = (vertical ? [page] : shownPages).filter((p) => (byPage.get(p)?.length ?? 0) > 0)
    if (tabbed.length === 0) return null
    return (
      <div className="notes-page-tabs notes-surface">
        {tabbed.map((p) => (
          <PageTab
            key={`${chapterId}-${p}`}
            page={p}
            count={byPage.get(p)?.length ?? 0}
            chapterId={chapterId}
            seriesId={seriesId}
            pageCount={pageCount}
            onGoToPage={onGoToPage}
          />
        ))}
      </div>
    )
  }

  const shared = { workspace, fold, pageCount, onGoToPage }

  if (geometry.kind === 'paged') {
    return (
      <aside
        className="notes-margin notes-surface"
        data-lift={lift}
        onClick={keepToNotes}
        style={
          {
            left: geometry.x,
            width: width + GAP,
            top: geometry.top + 12,
            maxHeight: Math.max(160, geometry.bottom - geometry.top - 24),
            '--notes-top': `${geometry.top + 12}px`,
          } as CSSProperties
        }
      >
        {[...shownPages]
          .sort((a, b) => a - b)
          .map((p) => (
            <PageNotes key={`${chapterId}-${p}`} page={p} notes={byPage.get(p) ?? []} {...shared} />
          ))}
        <ChapterNotes key={chapterId} notes={wholeChapter} {...shared} />
      </aside>
    )
  }

  const groups = [...geometry.tops].filter(([p]) => byPage.has(p) || (p === 0 && wholeChapter.length > 0))
  return (
    <>
      {createPortal(
        <div ref={column} className="notes-strip-column" style={{ left: geometry.edge, width: width + GAP }}>
          {groups.map(([p, top]) => (
            <div
              key={`${chapterId}-${p}`}
              className="notes-strip-group notes-surface"
              data-top={top}
              style={{ top }}
              onClick={keepToNotes}
            >
              {p === 0 && <ChapterNotes notes={wholeChapter} {...shared} />}
              {byPage.has(p) && <PageNotes page={p} notes={byPage.get(p) ?? []} addable={false} {...shared} />}
            </div>
          ))}
        </div>,
        geometry.host,
      )}
      <StripAdd page={page} workspace={workspace} lift={lift} style={{ left: geometry.x + GAP, width }} />
    </>
  )
}
