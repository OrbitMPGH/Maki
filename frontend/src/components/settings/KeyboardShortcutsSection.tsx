import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import { ActionIcon, Button, Group, Text, Tooltip, VisuallyHidden } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { plural, t as now } from '@lingui/core/macro'
import { IconArrowBackUp, IconPlus, IconX } from '../../lib/icons'
import { notifications } from '../../lib/toast'
import {
  actionLabel,
  assignKeys,
  comboForAction,
  comboFromEvent,
  defaultKeys,
  holdersOf,
  isReadingArrow,
  isSideArrow,
  MAX_KEYS_PER_ACTION,
  resolveBindings,
  SHORTCUT_ACTIONS,
  SHORTCUT_GROUP_LABELS,
  SHORTCUT_GROUPS,
  toOverrides,
  useSaveShortcutSettings,
  useShortcutSettings,
  type ShortcutActionId,
  type ShortcutBindings,
} from '../../lib/shortcuts'
import { useFormatCombo } from '../../lib/shortcutLabels'
import { SettingsSection } from '../../pages/settings/SettingsSection'

const sameKeys = (a: string[], b: string[]) => a.length === b.length && a.every((key, i) => key === b[i])

/** Every rebindable key, grouped as the reader's shortcut sheet groups them. Saves on each change. */
export function KeyboardShortcutsSection() {
  const { t, i18n } = useLingui()
  const { data, isError, refetch, isFetching } = useShortcutSettings()
  const save = useSaveShortcutSettings()
  const formatCombo = useFormatCombo()
  const bindings = resolveBindings(data?.bindings)
  const [recording, setRecording] = useState<ShortcutActionId | null>(null)
  const [status, setStatus] = useState('')
  // Until the saved set arrives nothing can be edited: an edit made over the defaults would save
  // them over whatever the user had.
  const loaded = Boolean(data)
  const changed = Object.keys(toOverrides(bindings)).length > 0
  const label = (id: ShortcutActionId) => i18n._(actionLabel(id))

  // An edit unmounts the control that made it (a chip's x, the reset arrow, the recorder), so focus
  // goes to that row's add button, or the first row's after Reset all.
  const listRef = useRef<HTMLDivElement>(null)
  const refocus = useRef<ShortcutActionId | 'first' | null>(null)
  useLayoutEffect(() => {
    const target = refocus.current
    if (!target || recording) return
    // Wait while the control that made the edit still has focus: the save may not have landed yet.
    const active = document.activeElement
    if (active && active !== document.body) {
      // Reset all sits in the header and goes once nothing differs, so it waits outside the list too.
      if (target !== 'first' && !listRef.current?.contains(active)) refocus.current = null
      return
    }
    refocus.current = null
    const row = target === 'first' ? '' : `[data-shortcut-row="${target}"] `
    listRef.current?.querySelector<HTMLElement>(`${row}[data-shortcut-add]`)?.focus()
  })

  const commit = (next: ShortcutBindings, focusOn: ShortcutActionId | 'first') => {
    refocus.current = focusOn
    save.mutate(toOverrides(next), {
      onError: () => notifications.show({ message: now`Couldn't save the shortcuts`, color: 'var(--danger)' }),
    })
  }

  const announceMoves = (moved: { id: ShortcutActionId; combo: string }[], to: ShortcutActionId) => {
    for (const { id, combo } of moved) {
      const key = formatCombo(combo)
      const from = label(id)
      const target = label(to)
      notifications.show({ message: now`${key} moved from ${from} to ${target}` })
    }
  }

  const record = (id: ShortcutActionId, pressed: string) => {
    setRecording(null)
    refocus.current = id
    const combo = comboForAction(id, pressed)
    const key = formatCombo(combo)
    const target = label(id)
    if (bindings[id].includes(combo)) {
      setStatus(t`${key} is already on ${target}`)
      return
    }
    // A plain side arrow would also be one of the page-turn arrows in some reading direction.
    if (isSideArrow(combo) && holdersOf(bindings, combo, id).some((holder) => isReadingArrow(holder.combo))) {
      const message = t`The left and right arrows turn pages. Remove them from Next page and Previous page first.`
      setStatus(message)
      notifications.show({ message, color: 'var(--warn)' })
      return
    }
    const { next, moved } = assignKeys(bindings, id, [...bindings[id], combo])
    announceMoves(moved, id)
    setStatus(moved.length > 0 ? t`${key} moved to ${target}` : t`${key} added to ${target}`)
    commit(next, id)
  }

  const remove = (id: ShortcutActionId, combo: string) => {
    const key = formatCombo(combo)
    const target = label(id)
    setStatus(t`${key} removed from ${target}`)
    commit({ ...bindings, [id]: bindings[id].filter((held) => held !== combo) }, id)
  }

  const resetRow = (id: ShortcutActionId) => {
    const { next, moved } = assignKeys(bindings, id, defaultKeys(id))
    announceMoves(moved, id)
    const target = label(id)
    setStatus(t`${target} is back to its default keys`)
    commit(next, id)
  }

  const resetAll = () => {
    const previous = bindings
    setStatus(t`Every shortcut is back to its default keys`)
    commit(resolveBindings(undefined), 'first')
    const toast = notifications.show({
      autoClose: 8000,
      message: (
        <Group gap="xs" wrap="wrap">
          <Text size="sm">{now`Every shortcut is back to its default keys.`}</Text>
          <Button
            size="xs"
            variant="subtle"
            onClick={() => {
              notifications.hide(toast)
              save.mutate(toOverrides(previous))
            }}
          >
            {now`Undo`}
          </Button>
        </Group>
      ),
    })
  }

  const atMost = plural(MAX_KEYS_PER_ACTION, { one: '# key at most', other: '# keys at most' })

  return (
    <SettingsSection
      id="keyboard-shortcuts"
      title={<Trans>Keyboard shortcuts</Trans>}
      actions={
        changed &&
        loaded && (
          <Button size="xs" variant="subtle" onClick={resetAll}>
            <Trans>Reset all</Trans>
          </Button>
        )
      }
      panelProps={{ p: 0 }}
    >
      <VisuallyHidden role="status" aria-live="polite">
        {status}
      </VisuallyHidden>
      {isError && !data && (
        <Group className="shortcut-load-error" gap="sm" justify="space-between">
          <Text size="sm" c="var(--danger-fg)">
            <Trans>Couldn't load your shortcuts.</Trans>
          </Text>
          <Button size="xs" variant="default" loading={isFetching} onClick={() => void refetch()}>
            <Trans>Try again</Trans>
          </Button>
        </Group>
      )}
      <div ref={listRef}>
        {SHORTCUT_GROUPS.map((group) => (
          <div key={group} className="shortcut-group">
            <h3 className="shortcut-group-title">{i18n._(SHORTCUT_GROUP_LABELS[group])}</h3>
            {group === 'pages' && (
              <p className="shortcut-group-hint">
                <Trans>Forward and back arrows follow the series' reading direction.</Trans>
              </p>
            )}
            <ul className="shortcut-list">
              {SHORTCUT_ACTIONS.filter((action) => action.group === group).map((action) => {
                const keys = bindings[action.id]
                const name = i18n._(action.label)
                const isDefault = sameKeys(keys, defaultKeys(action.id))
                const full = keys.length >= MAX_KEYS_PER_ACTION
                const isRecording = recording === action.id
                return (
                  <li
                    key={action.id}
                    className="shortcut-row"
                    data-shortcut-row={action.id}
                    data-recording={isRecording || undefined}
                  >
                    <span className="shortcut-label">{name}</span>
                    <span className="shortcut-keys">
                      {keys.length === 0 && !isRecording && (
                        <span className="shortcut-none">
                          <Trans>No key</Trans>
                        </span>
                      )}
                      {keys.map((combo) => {
                        const keyName = formatCombo(combo)
                        return (
                          <span key={combo} className="shortcut-key">
                            <kbd>{keyName}</kbd>
                            <button
                              type="button"
                              className="shortcut-key-remove"
                              disabled={!loaded}
                              onClick={() => remove(action.id, combo)}
                              aria-label={t`Remove ${keyName} from ${name}`}
                            >
                              <IconX size={12} />
                            </button>
                          </span>
                        )
                      })}
                      {isRecording ? (
                        <KeyRecorder
                          onKey={(combo) => record(action.id, combo)}
                          onCancel={() => {
                            refocus.current = action.id
                            setRecording(null)
                          }}
                          label={t`Press a key for ${name}. Escape cancels.`}
                        />
                      ) : (
                        <Tooltip label={full ? atMost : t`Add a key`} withArrow position="left">
                          <ActionIcon
                            variant="subtle"
                            color="gray"
                            size="md"
                            data-shortcut-add
                            disabled={!loaded}
                            data-disabled={full || undefined}
                            aria-disabled={full || undefined}
                            onClick={() => {
                              if (!full) setRecording(action.id)
                            }}
                            aria-label={full ? t`${name} already has the most keys it can take` : t`Add a key to ${name}`}
                          >
                            <IconPlus size={14} />
                          </ActionIcon>
                        </Tooltip>
                      )}
                      <span className="shortcut-reset">
                        {!isDefault && (
                          <Tooltip label={t`Back to the default`} withArrow position="left">
                            <ActionIcon
                              variant="subtle"
                              color="gray"
                              size="md"
                              disabled={!loaded}
                              onClick={() => resetRow(action.id)}
                              aria-label={t`Reset ${name} to its default keys`}
                            >
                              <IconArrowBackUp size={14} />
                            </ActionIcon>
                          </Tooltip>
                        )}
                      </span>
                    </span>
                  </li>
                )
              })}
            </ul>
          </div>
        ))}
      </div>
    </SettingsSection>
  )
}

/**
 * Waits for one key press and reports it. Takes the press before anything else on the page can act
 * on it, so recording Ctrl+K does not also open search. Escape, or leaving the field, cancels.
 *
 * A plain key is reported on its key-up, which is swallowed too: reported on key-down, the Space
 * key-up would land on the add button that takes focus back and start recording again. A key held
 * with Ctrl, Alt or Cmd is reported at once, since a Mac sends no key-up while Cmd is down.
 */
function KeyRecorder({ onKey, onCancel, label }: { onKey: (combo: string) => void; onCancel: () => void; label: string }) {
  const ref = useRef<HTMLButtonElement>(null)
  const latest = useRef({ onKey, onCancel })
  latest.current = { onKey, onCancel }

  useEffect(() => {
    ref.current?.focus()
    let pending: string | null = null
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Tab') {
        latest.current.onCancel()
        return
      }
      event.preventDefault()
      event.stopPropagation()
      if (event.key === 'Escape') {
        latest.current.onCancel()
        return
      }
      const combo = comboFromEvent(event)
      if (!combo || event.repeat) return
      if (/^(Mod|Ctrl|Meta|Alt)\+/.test(combo)) latest.current.onKey(combo)
      else pending = combo
    }
    const onKeyUp = (event: KeyboardEvent) => {
      if (!pending) return
      event.preventDefault()
      event.stopPropagation()
      const combo = pending
      pending = null
      latest.current.onKey(combo)
    }
    window.addEventListener('keydown', onKeyDown, true)
    window.addEventListener('keyup', onKeyUp, true)
    return () => {
      window.removeEventListener('keydown', onKeyDown, true)
      window.removeEventListener('keyup', onKeyUp, true)
    }
  }, [])

  return (
    <button
      ref={ref}
      type="button"
      className="shortcut-recorder"
      aria-label={label}
      onBlur={() => latest.current.onCancel()}
      onClick={() => latest.current.onCancel()}
    >
      <span className="shortcut-recorder-dot" aria-hidden />
      <Trans>Press a key, Esc cancels</Trans>
    </button>
  )
}
