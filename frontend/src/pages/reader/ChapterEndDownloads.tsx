import { Plural, Trans } from '@lingui/react/macro'
import { queueItemLabel } from '../../api/queue'
import type { QueueItemDto } from '../../api/types'
import { activeDownloads } from './endScreen'

/** What the series' queued chapters are doing, for a reader parked on the last chapter on disk. */
export default function ChapterEndDownloads({ items }: { items: QueueItemDto[] }) {
  const { working, waiting } = activeDownloads(items)
  const lead = working[0]
  const more = Math.max(0, working.length - 1) + waiting.length
  if (!lead && more === 0) return null

  const label = lead ? queueItemLabel(lead) : null
  const percent = lead && lead.pagesTotal > 0 ? Math.round((lead.pagesDone / lead.pagesTotal) * 100) : null
  return (
    <span className="reader-end-note reader-end-downloads" role="status">
      {lead ? (
        <>
          {percent != null ? (
            <Trans>
              Downloading {label}, {percent}%.
            </Trans>
          ) : (
            <Trans>Downloading {label}.</Trans>
          )}
          {more > 0 && (
            <>
              {' '}
              <Plural value={more} one="# more is queued." other="# more are queued." />
            </>
          )}
        </>
      ) : (
        <Plural value={more} one="# chapter is queued to download." other="# chapters are queued to download." />
      )}
    </span>
  )
}
