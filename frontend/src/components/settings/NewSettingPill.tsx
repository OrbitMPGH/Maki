import { Badge } from '@mantine/core'
import { Trans } from '@lingui/react/macro'
import { useIsNewSetting } from '../../pages/settings/newSettings'

/** Marks a settings card or control added since the version this instance upgraded from. */
export function NewSettingPill({ id }: { id: string }) {
  if (!useIsNewSetting(id)) return null
  return (
    <Badge size="sm" variant="light" color="var(--brand)" className="new-setting-pill">
      <Trans context="recently added setting">New</Trans>
    </Badge>
  )
}
