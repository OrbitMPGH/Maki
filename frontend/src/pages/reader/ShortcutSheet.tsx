import { ActionIcon, Anchor, FocusTrap } from '@mantine/core'
import { Link } from 'react-router-dom'
import { IconX } from '../../lib/icons'
import { useEffect, useMemo } from 'react'
import { Trans, useLingui } from '@lingui/react/macro'
import { SHORTCUT_ACTIONS, SHORTCUT_GROUP_LABELS, SHORTCUT_GROUPS, useShortcutBindings } from '../../lib/shortcuts'
import { useFormatCombo } from '../../lib/shortcutLabels'

interface Shortcut {
  id: string
  keys: string[]
  label: string
}

const GROUPS = SHORTCUT_GROUPS.filter((group) => group !== 'app')

/**
 * Every key the reader answers to, on one sheet. Opened with `?` or the keyboard button; the page
 * handles the keys, including Escape, so this only draws and takes the backdrop click.
 */
export default function ShortcutSheet({ rtl, onClose }: { rtl: boolean; onClose: () => void }) {
  const { t, i18n } = useLingui()
  const bindings = useShortcutBindings()
  const formatCombo = useFormatCombo()
  // The trap moves focus into the sheet and keeps Tab inside it; this hands focus back to whatever
  // opened it, which is otherwise lost to the body.
  useEffect(() => {
    const opener = document.activeElement as HTMLElement | null
    return () => opener?.focus?.()
  }, [])

  const groups = useMemo(
    () =>
      GROUPS.map((group) => {
        const items: Shortcut[] = SHORTCUT_ACTIONS.filter((action) => action.group === group).map((action) => ({
          id: action.id,
          label: i18n._(action.label),
          keys: bindings[action.id].map((combo) => formatCombo(combo, { rtl })),
        }))
        if (group === 'other')
          items.push({
            id: 'escape',
            label: t`Leave the reader`,
            keys: [formatCombo('Escape')],
          })
        return { title: i18n._(SHORTCUT_GROUP_LABELS[group]), items }
      }),
    [bindings, formatCombo, rtl, i18n, i18n.locale, t],
  )

  return (
    <div className="reader-shortcuts" onClick={onClose}>
      <FocusTrap>
        <div
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
                    <div key={item.id} className="reader-shortcuts-row">
                      <dt>{item.label}</dt>
                      <dd>
                        {item.keys.length === 0 ? (
                          <span className="reader-shortcuts-none">
                            <Trans>No key</Trans>
                          </span>
                        ) : (
                          item.keys.map((key) => <kbd key={key}>{key}</kbd>)
                        )}
                      </dd>
                    </div>
                  ))}
                </dl>
              </section>
            ))}
          </div>
          <div className="reader-shortcuts-foot">
            <Anchor component={Link} to="/settings?tab=reading&s=keyboard-shortcuts" size="sm">
              <Trans>Change keys</Trans>
            </Anchor>
          </div>
        </div>
      </FocusTrap>
    </div>
  )
}
