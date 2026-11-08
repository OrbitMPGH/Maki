import type { ComponentProps, ReactNode } from 'react'
import { CloseButton, Popover } from '@mantine/core'
import { useLingui } from '@lingui/react/macro'
import { NotesPanelBody } from './NotesPanel'

type BodyProps = Omit<ComponentProps<typeof NotesPanelBody>, 'takeFocus'>

function Head({ title, extra, onClose }: { title: string; extra?: ReactNode; onClose: () => void }) {
  const { t } = useLingui()
  return (
    <div className="notes-dropdown-head">
      <span className="notes-dropdown-title">{title}</span>
      {extra}
      <CloseButton size="sm" className="notes-close" aria-label={t`Close notes`} onClick={onClose} />
    </div>
  )
}

/**
 * The reader's notes list on a wider screen: a panel hanging under the toolbar's Notes button. It
 * leaves the page readable beside it and stays up while pages turn, so it has no overlay and no
 * outside-click close.
 */
export function NotesDropdown({
  opened,
  onClose,
  title,
  headerExtra,
  zIndex,
  ...body
}: BodyProps & {
  opened: boolean
  onClose: () => void
  title: string
  headerExtra?: ReactNode
  zIndex?: number
}) {
  if (!opened) return null
  return (
    <section className="notes-dropdown notes-dropdown-fixed notes-surface" role="dialog" aria-label={title} style={{ zIndex }}>
      <Head title={title} extra={headerExtra} onClose={onClose} />
      <NotesPanelBody {...body} takeFocus />
    </section>
  )
}

/**
 * The same list opened from a chapter row on the series page, as the dropdown of a `Popover` whose
 * target is the row's note count, so the dialog is named after that count.
 */
export function ChapterNotesDropdown({
  chapterId,
  seriesId,
  chapterLabel,
  spoilerGuard,
  onClose,
}: {
  chapterId: number
  seriesId: number
  chapterLabel: string
  spoilerGuard: boolean
  onClose: () => void
}) {
  const { t } = useLingui()
  return (
    <Popover.Dropdown className="notes-dropdown notes-surface" p={0}>
      <Head title={t`Notes, ${chapterLabel}`} onClose={onClose} />
      <NotesPanelBody chapterId={chapterId} seriesId={seriesId} spoilerGuard={spoilerGuard} />
    </Popover.Dropdown>
  )
}
