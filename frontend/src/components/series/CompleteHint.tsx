import { Button } from '@mantine/core'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { IconCircleCheck } from '../../lib/icons'
import { SeriesHint } from './SeriesHint'

/**
 * A quiet line for a finished series whose every kept chapter is read, while some were switched off or
 * a chapter was still expected when the last one was read. Switched-off chapters may be skipped extras
 * or the rest of a series being tried out, so Maki asks instead of guessing.
 */
export function CompleteHint({
  leftOut,
  onComplete,
  onDismiss,
}: {
  leftOut: number
  onComplete: () => void
  onDismiss: () => void
}) {
  const { t } = useLingui()
  return (
    <SeriesHint
      icon={IconCircleCheck}
      dismissLabel={t`Not yet`}
      onDismiss={onDismiss}
      actions={
        <Button size="compact-sm" variant="subtle" onClick={onComplete}>
          <Trans>Mark completed</Trans>
        </Button>
      }
    >
      {leftOut > 0 ? (
        <Plural
          value={leftOut}
          one="You've read every chapter you kept. # chapter was left out. Done with this series?"
          other="You've read every chapter you kept. # chapters were left out. Done with this series?"
        />
      ) : (
        <Trans>You've read every chapter. Done with this series?</Trans>
      )}
    </SeriesHint>
  )
}
