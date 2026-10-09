import { msg } from '@lingui/core/macro'
import { t as now } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import type { QueueItemDto, QueueOrigin } from './types'
import { upgradeReasonLabel } from './upgrades'

const QUEUE_ORIGINS: QueueOrigin[] = [
  'unknown',
  'manual',
  'smartdownload',
  'monitorrefresh',
  'requestapproval',
  'healthrepair',
  'upgrade',
]

/** A newer server can send an origin this build has no case for; treat it as 'unknown' rather than crash. */
export function queueOriginOrUnknown(value: string): QueueOrigin {
  return (QUEUE_ORIGINS as string[]).includes(value) ? (value as QueueOrigin) : 'unknown'
}

/**
 * What a queue row says, worded here rather than by the server.
 *
 * A queue update is broadcast over SignalR to every connected client at once, and those clients do
 * not share a language, so the server sends the parts and each client puts them together. That is
 * the same call the plan makes for chapter labels and failure reasons alike, and it is why
 * `QueueItemDto` carries `chapterNumber`, `errorKey` and the rest instead of two sentences.
 */

/**
 * Reasons Maki itself worded, keyed by the `error.download.*` name the server stores on the row.
 *
 * Descriptors, not strings: this table is built once when the module loads, and a rendered string
 * would be stuck in whichever language was active then. Rendered at the call site instead.
 *
 * The same English also lives in `locales/en/server.po` under these same keys, and both copies are
 * live: the queue row is worded here, while the failure notification and the inbox entry are worded
 * on the server, where the row is a `select` inside a longer sentence. Two catalogues are two
 * Weblate components, so the duplicate costs a translator one extra entry and nothing else. Change
 * one and change the other.
 */
const ERROR_LABELS: Record<string, MessageDescriptor> = {
  'error.download.noPages': msg`The source returned no pages`,
  'error.download.approvedSourceNoPages': msg`The approved source no longer has these pages`,
  'error.download.noMoreSources': msg`Every source failed for this chapter`,
  'error.download.unexpected': msg`Something went wrong, Maki will try again`,
  'error.download.rateLimited': msg({
    message: `Rate limited by {source}, waiting before the next try`,
    comment: `{source} is a site name (MangaDex, Weeb Central) and is never translated. The row
      shows when the next attempt is as its own field, so it is not repeated here.`,
  }),
  'error.download.earlyAccess': msg({
    message: `Still early access on {source}, Maki will check again later`,
    comment: `{source} is a site name and is never translated.`,
  }),
  'error.download.resolveTimedOut': msg`Took too long to find a source`,
  'error.download.repairNeedsReview': msg`This repair needs a source chosen by hand`,
  'error.download.chapterGone': msg`That chapter no longer exists`,
  'error.download.noChapterToResolve': msg`This queue item has no chapter to find a source for`,
  'error.download.importRejected': msg`Import rejected, the library was left as it was`,
  'error.download.torrentMissing': msg`The torrent never appeared in qBittorrent`,
  'error.download.upgradeTargetGone': msg`The file this upgrade was meant to replace is gone`,
  'error.download.upgradeMoveFailed': msg`The old file could not be moved aside for the upgrade`,
  'error.download.pickedSourceUnavailable': msg`The source you picked could not provide this chapter`,
  'error.download.rootFolderUnavailable': msg`The library folder is not available, check that its drive or share is mounted`,
  'error.download.torrentRemoved': msg`The torrent is no longer in qBittorrent`,
  'error.download.noEnabledMapping': msg`This series has no enabled sources`,
  'error.download.healthReviewActive': msg`A health review is active for this series`,
  'error.download.challengeNotSolved': msg`The source needs FlareSolverr to get past its anti-bot check (Settings → Downloads)`,
  'error.download.sourceRejected': msg`The source refused the request`,
  'error.download.sourceError': msg`The source answered with an error`,
  'error.download.network': msg`Could not reach the source`,
  'error.download.disk': msg`Could not write the chapter to disk`,
  'error.download.invalidPage': msg`A page the source sent is not a readable image`,
  'error.download.notListed': msg`No source has this chapter right now`,
  'error.upgrades.volumeGuard': msg`Held back for review, {file}: {reason}`,
}

/**
 * What names a row: the release title for a torrent grab, the chapter's own title for a one-shot,
 * otherwise the volume and chapter numbers.
 *
 * Numbers arrive as strings and are not reformatted. They are identifiers, and a chapter written
 * "12,5" would no longer match the one on disk.
 */
export function queueItemLabel(item: QueueItemDto): string {
  if (item.releaseTitle) return item.releaseTitle
  if (item.chapterNumber === null) return item.chapterTitle ?? now`One-shot`
  const chapter = item.chapterNumber
  if (item.chapterVolume === null) return now`Ch.${chapter}`
  const volume = item.chapterVolume
  return now`Vol.${volume} Ch.${chapter}`
}

/**
 * Why the row stopped, or null when it has not.
 *
 * A row with no `errorKey` carries text Maki did not write: a scraper's or a torrent client's own
 * words, or English from before the queue was keyed. Both are shown as they arrived, because
 * translating somebody else's error would mean parsing it first.
 */
export function queueErrorMessage(
  item: QueueItemDto,
  render: (m: MessageDescriptor) => string,
): string | null {
  if (!item.errorKey) return item.errorMessage
  const label = ERROR_LABELS[item.errorKey]
  // A key this build has no case for: newer server, older page. Its own English is on the row only
  // when it was never keyed, so there is nothing better to show than nothing.
  if (!label) return item.errorMessage
  let reason: string
  if (!item.errorParams) {
    reason = render(label)
  } else {
    const values =
      item.errorKey === 'error.upgrades.volumeGuard'
        ? { ...item.errorParams, reason: upgradeReasonLabel(render, String(item.errorParams.reason ?? '')) }
        : item.errorParams
    reason = render({ ...label, values })
  }
  const detail = item.errorMessage
  return detail ? render({ ...WITH_DETAIL, values: { reason, detail } }) : reason
}

/** A keyed reason followed by what the source or the error itself said, which is never translated. */
const WITH_DETAIL = msg({
  message: `{reason}: {detail}`,
  comment: `{reason} is one of the download failure reasons above, already translated. {detail} is
    the site's own error text, an HTTP status or a file name, shown as it arrived.`,
})
