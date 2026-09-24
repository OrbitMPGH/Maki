import { useCallback } from 'react'
import { msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { CONTENT_RATING_LABELS as RATING_LABELS } from '../../api/hooks'
import { TYPE_LABELS as BASE_TYPE_LABELS } from '../../components/CatalogueFilters'
import { useLabel } from '../../i18n-context'

export { GENRE_LABELS } from '../../components/CatalogueFilters'

// Descriptors, rendered at the call site with `useLabel()`. A name with no entry here is data and
// passes through as-is: `LABELS[name] ?? name`.

/** Series types, with the "Unknown" bucket `stats/library` adds for series with no type. */
export const TYPE_LABELS: Record<string, MessageDescriptor> = {
  ...BASE_TYPE_LABELS,
  unknown: msg`Unknown`,
}

/** Content ratings as `stats/library` groups them, with "unknown" for series that have none. */
export const CONTENT_RATING_LABELS: Record<string, MessageDescriptor> = {
  ...RATING_LABELS,
  unknown: msg`Unrated`,
}

/**
 * Source reliability rows are named after the source, or after the protocol when the grab had no
 * source (a torrent or usenet release). The protocol arrives lowercased.
 */
export const PROTOCOL_LABELS: Record<string, MessageDescriptor> = {
  scraper: msg`Scraper`,
  torrent: msg`Torrent`,
  usenet: msg`Usenet`,
  unknown: msg`Unknown source`,
}

/** Series status as `stats/library` sends it (the enum name, looked up lowercased). */
export const STATUS_LABELS: Record<string, MessageDescriptor> = {
  ongoing: msg`Ongoing`,
  completed: msg`Completed`,
  hiatus: msg`Hiatus`,
  cancelled: msg`Cancelled`,
  unknown: msg`Unknown`,
}

/**
 * Renders a wire name through one of the tables above. The server is not consistent about case
 * ("Ongoing", "manga", "unknown"), so the lookup tries the name as sent, then lowercased. Anything
 * with no entry is data and comes back untouched.
 */
export function useNameLabel(): (table: Record<string, MessageDescriptor>, name: string) => string {
  const label = useLabel()
  return useCallback(
    (table, name) => {
      const descriptor = table[name] ?? table[name.toLowerCase()]
      return descriptor ? label(descriptor) : name
    },
    [label],
  )
}
