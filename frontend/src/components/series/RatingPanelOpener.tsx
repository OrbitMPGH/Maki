import { useState, type ReactNode } from 'react'
import { Drawer, Popover } from '@mantine/core'
import { useLingui } from '@lingui/react/macro'
import { usePhoneSheet } from '../notes/phone'
import { RatingPanel } from './RatingPanel'

/** Fixed, so the panel never resizes (and moves) as its content changes. */
const PANEL_WIDTH = 384

type PanelProps = Parameters<typeof RatingPanel>[0]

/**
 * The rating panel under its trigger, as a popover on a wide screen and a bottom sheet on a phone.
 * `target` receives the toggle so any button can open it.
 */
export function RatingPanelOpener({
  target,
  panel,
  zIndex,
}: {
  target: (toggle: () => void, opened: boolean) => ReactNode
  panel: PanelProps
  /** Above the default, for a trigger inside the reader, which sits over Mantine's own layers. */
  zIndex?: number
}) {
  const { t } = useLingui()
  const phone = usePhoneSheet()
  const [opened, setOpened] = useState(false)
  const toggle = () => setOpened((o) => !o)
  const body = <RatingPanel {...panel} />

  return phone ? (
    <>
      {target(toggle, opened)}
      <Drawer
        opened={opened}
        onClose={() => setOpened(false)}
        position="bottom"
        size="auto"
        zIndex={zIndex}
        title={t`Your rating`}
        classNames={{ content: 'rating-panel-drawer', body: 'rating-panel-sheet' }}
      >
        {body}
      </Drawer>
    </>
  ) : (
    <Popover opened={opened} onChange={setOpened} position="bottom-start" width={PANEL_WIDTH} withArrow shadow="md" trapFocus zIndex={zIndex}>
      <Popover.Target>{target(toggle, opened)}</Popover.Target>
      <Popover.Dropdown>{body}</Popover.Dropdown>
    </Popover>
  )
}
