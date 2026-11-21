import { Button } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { IconArchive } from '../../lib/icons'
import { SeriesHint } from './SeriesHint'

/** A quiet line for a completed series everyone has finished: offers the archive and can be waved off for good. */
export function ArchiveHint({ onArchive, onDismiss }: { onArchive: () => void; onDismiss: () => void }) {
  const { t } = useLingui()
  return (
    <SeriesHint
      icon={IconArchive}
      dismissLabel={t`Dismiss`}
      onDismiss={onDismiss}
      actions={
        <Button size="compact-sm" variant="subtle" onClick={onArchive}>
          <Trans context="series">Archive</Trans>
        </Button>
      }
    >
      <Trans>You've finished this. Archive it?</Trans>
    </SeriesHint>
  )
}
