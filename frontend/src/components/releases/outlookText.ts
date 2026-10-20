import { msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import type { ConfidenceLevel, RefreshTier } from '../../api/releases'

/** Why the next check is when it is, one line under the outlook. */
export const REFRESH_TIER_TEXT: Record<RefreshTier, MessageDescriptor> = {
  Fixed: msg`Checked every 30 minutes.`,
  Unknown: msg`Checked every 6 hours until it has some release history.`,
  JustReleased: msg`Checked every 30 minutes for a few hours after a new chapter.`,
  Active: msg`Checked every 2 hours while chapters keep coming.`,
  Slowing: msg`Checked every 6 hours after two weeks without a chapter.`,
  Quiet: msg`Checked once a day while it is quiet.`,
  Announced: msg`Checked right after the announced time.`,
}

export const CONFIDENCE_LABEL: Record<ConfidenceLevel, MessageDescriptor> = {
  high: msg`High confidence`,
  medium: msg`Medium confidence`,
  low: msg`Low confidence`,
}
