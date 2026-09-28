import { ActionIcon } from '@mantine/core'
import { IconX } from '@tabler/icons-react'
import { useEffect, useRef } from 'react'
import { Trans, useLingui } from '@lingui/react/macro'

interface Shortcut {
  keys: string[]
  label: string
}

/**
 * Every key the reader answers to, on one sheet. Opened with `?` or the keyboard button; the page
 * handles the keys, including Escape, so this only draws and takes the backdrop click.
 */
export default function ShortcutSheet({ rtl, onClose }: { rtl: boolean; onClose: () => void }) {
  const { t } = useLingui()
  const panelRef = useRef<HTMLDivElement>(null)
  useEffect(() => panelRef.current?.focus(), [])

  // The arrows follow the reading direction, same as the handler that reads them.
  const forward = rtl ? '←' : '→'
  const back = rtl ? '→' : '←'
  const groups: { title: string; items: Shortcut[] }[] = [
    {
      title: t`Turning pages`,
      items: [
        { keys: [forward, t`Space`], label: t`Next page` },
        { keys: [back, t`Shift + Space`], label: t`Previous page` },
        { keys: [t`Home`], label: t`First page` },
        { keys: [t`End`], label: t`Last page` },
      ],
    },
    {
      title: t`Layout`,
      items: [
        { keys: ['1'], label: t`Single page` },
        { keys: ['2'], label: t`Double page` },
        { keys: ['3'], label: t`Continuous` },
        { keys: ['D'], label: t`Switch reading direction` },
        { keys: ['+', '−'], label: t`Zoom in and out` },
        { keys: ['0'], label: t`Reset zoom` },
      ],
    },
    {
      title: t`Everything else`,
      items: [
        { keys: ['F'], label: t`Full screen` },
        { keys: ['T'], label: t`Page thumbnails` },
        { keys: ['B'], label: t`Bookmark this page` },
        { keys: [t`Esc`], label: t`Back to series` },
        { keys: ['?'], label: t`Show or hide this list` },
      ],
    },
  ]

  return (
    <div className="reader-shortcuts" onClick={onClose}>
      <div
        ref={panelRef}
        className="reader-shortcuts-panel"
        role="dialog"
        aria-modal="true"
        aria-labelledby="reader-shortcuts-title"
        tabIndex={-1}
        onClick={(event) => event.stopPropagation()}
      >
        <div className="reader-shortcuts-head">
          <h2 id="reader-shortcuts-title">
            <Trans>Keyboard shortcuts</Trans>
          </h2>
          <ActionIcon variant="subtle" color="gray" className="reader-end-quiet" onClick={onClose} aria-label={t`Close`}>
            <IconX size={16} />
          </ActionIcon>
        </div>
        <div className="reader-shortcuts-groups">
          {groups.map((group) => (
            <section key={group.title}>
              <h3>{group.title}</h3>
              <dl>
                {group.items.map((item) => (
                  <div key={item.label} className="reader-shortcuts-row">
                    <dt>{item.label}</dt>
                    <dd>
                      {item.keys.map((key) => (
                        <kbd key={key}>{key}</kbd>
                      ))}
                    </dd>
                  </div>
                ))}
              </dl>
            </section>
          ))}
        </div>
      </div>
    </div>
  )
}
