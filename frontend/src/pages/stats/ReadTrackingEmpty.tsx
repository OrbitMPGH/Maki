import { useLingui } from '@lingui/react/macro'
import { EmptyState } from '../../components/ui/EmptyState'

/** Shared by the reading sections while read tracking is off: the built-in reader or Kavita turns it on. */
export function ReadTrackingEmpty() {
  const { t } = useLingui()
  return (
    <EmptyState
      compact
      title={t`No reading tracked yet`}
      description={t`Read a chapter in Maki's reader and it starts tracking. An admin can also connect Kavita in Settings. Downloads and library changes are tracked either way.`}
    />
  )
}
