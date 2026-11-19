import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import { ActionIcon, Button, Group, Modal, Stack, Text, Tooltip, VisuallyHidden } from '@mantine/core'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { plural, t as now } from '@lingui/core/macro'
import { IconArrowBackUp, IconKeyboard, IconPlus, IconX } from '../../lib/icons'
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
import { twoColumns } from '../../lib/columns'
import { formatList } from '../../format'
import { SettingsSection } from '../../pages/settings/SettingsSection'
import { ModalFooter, ModalHeading } from '../ui/ModalShell'
import { SettingsGroupTitle } from './SettingsGroupTitle'

const sameKeys = (a: string[], b: string[]) => a.length === b.length && a.every((key, i) => key === b[i])

type ShortcutGroup = (typeof SHORTCUT_GROUPS)[number]

/**
 * Every rebindable key, grouped as the reader's shortcut sheet groups them. The card says which
 * actions have keys of their own; the list opens in a modal in two columns. Saves on each change.
 */
export function KeyboardShortcutsSection() {
  const { t, i18n } = useLingui()
  const { data, isError, refetch, isFetching } = useShortcutSettings()
  const save = useSaveShortcutSettings()
  const formatCombo = useFormatCombo()
  const bindings = resolveBindings(data?.bindings)
  const [recording, setRecording] = useState<ShortcutActionId | null>(null)
  const [status, setStatus] = useState('')
  const [open, setOpen] = useState(false)
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
      // Reset all sits outside the list and goes once nothing differs, so it waits outside the list too.
      if (target !== 'first' && !listRef.current?.contains(active)) refocus.current = null
      return
    }
    refocus.current = null
    const row = target === 'first' ? '' : `[data-shortcut-row="${target}"] `
    listRef.current?.querySelector<HTMLElement>(`${row}[data-shortcut-add]`)?.focus()
  })

  const saveFailed = () => notifications.show({ message: now`Couldn't save the shortcuts`, color: 'var(--danger)' })

  // Undo for Reset all, until the next edit: after one it would throw that edit away. The toast
  // offers it outside the modal; inside, the modal traps Tab, so its footer offers it too.
  const undoToast = useRef<string | null>(null)
  const [undoFrom, setUndoFrom] = useState<ShortcutBindings | null>(null)
  const dropUndo = () => {
    if (undoToast.current) notifications.hide(undoToast.current)
    undoToast.current = null
    setUndoFrom(null)
  }
  const undoReset = (previous: ShortcutBindings) => {
    dropUndo()
    refocus.current = 'first'
    setStatus(t`Your shortcuts are back`)
    save(toOverrides(previous), { onError: saveFailed })
  }

  const commit = (next: ShortcutBindings, focusOn: ShortcutActionId | 'first') => {
    dropUndo()
    refocus.current = focusOn
    save(toOverrides(next), { onError: saveFailed })
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
    setUndoFrom(previous)
    const toast = (undoToast.current = notifications.show({
      autoClose: 8000,
      message: (
        <Group gap="xs" wrap="wrap">
          <Text size="sm">{now`Every shortcut is back to its default keys.`}</Text>
          <Button
            size="xs"
            variant="subtle"
            onClick={() => undoReset(previous)}
          >
            {now`Undo`}
          </Button>
        </Group>
      ),
      onClose: () => {
        if (undoToast.current !== toast) return
        undoToast.current = null
        setUndoFrom(null)
      },
    }))
  }

  const close = () => {
    setRecording(null)
    setOpen(false)
  }

  const atMost = plural(MAX_KEYS_PER_ACTION, { one: '# key at most', other: '# keys at most' })
  const customised = SHORTCUT_ACTIONS.filter((action) => !sameKeys(bindings[action.id], defaultKeys(action.id)))
  const actionsIn = (group: ShortcutGroup) => SHORTCUT_ACTIONS.filter((action) => action.group === group)
  // A group's title is a row, and the page-turn hint another.
  const columns = twoColumns(SHORTCUT_GROUPS, (group) => actionsIn(group).length + (group === 'pages' ? 2 : 1))

  const renderGroup = (group: ShortcutGroup) => (
    <div key={group} className="shortcut-group">
      <h3 className="shortcut-group-title">{i18n._(SHORTCUT_GROUP_LABELS[group])}</h3>
      {group === 'pages' && (
        <p className="shortcut-group-hint">
          <Trans>Forward and back arrows follow the series' reading direction.</Trans>
        </p>
      )}
      <ul className="shortcut-list">
        {actionsIn(group).map((action) => {
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
                    onCancel={(returnFocus) => {
                      if (returnFocus) refocus.current = action.id
                      setRecording(null)
                    }}
                    action={name}
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
  )

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
    >
      {!open && (
        <VisuallyHidden role="status" aria-live="polite">
          {status}
        </VisuallyHidden>
      )}
      {isError && !data ? (
        <Group gap="sm" justify="space-between">
          <Text size="sm" c="var(--danger-fg)">
            <Trans>Couldn't load your shortcuts.</Trans>
          </Text>
          <Button size="xs" variant="default" loading={isFetching} onClick={() => void refetch()}>
            <Trans>Try again</Trans>
          </Button>
        </Group>
      ) : (
        <Group justify="space-between" wrap="wrap" gap="md">
          <Stack gap={2} className="settings-summary">
            <SettingsGroupTitle>
              {customised.length === 0 ? (
                <Trans>Every key is the default</Trans>
              ) : (
                <Plural
                  value={customised.length}
                  one="# action has keys of your own"
                  other="# actions have keys of your own"
                />
              )}
            </SettingsGroupTitle>
            <Text size="sm" c="var(--ink-3)">
              {customised.length > 0 && customised.length <= 3 ? (
                formatList(customised.map((action) => i18n._(action.label)))
              ) : (
                <Trans>The reader's keys and the search shortcut.</Trans>
              )}
            </Text>
          </Stack>
          <Button
            variant="default"
            leftSection={<IconKeyboard size={16} />}
            disabled={!loaded}
            onClick={() => setOpen(true)}
            style={{ flexShrink: 0 }}
          >
            <Trans>Edit shortcuts</Trans>
          </Button>
        </Group>
      )}

      <Modal
        opened={open}
        onClose={close}
        size={1080}
        // While a key is being recorded, Esc cancels the recording rather than closing the list.
        closeOnEscape={recording === null}
        title={<ModalHeading title={t`Keyboard shortcuts`} />}
      >
        <VisuallyHidden role="status" aria-live="polite">
          {status}
        </VisuallyHidden>
        <div ref={listRef} className="shortcut-columns">
          {columns.map((groups) => (
            <div key={groups[0]}>{groups.map(renderGroup)}</div>
          ))}
        </div>
        <ModalFooter
          status={
            undoFrom ? (
              <Button size="xs" variant="subtle" onClick={() => undoReset(undoFrom)}>
                <Trans>Undo reset</Trans>
              </Button>
            ) : changed && loaded ? (
              <Button size="xs" variant="subtle" onClick={resetAll}>
                <Trans>Reset all</Trans>
              </Button>
            ) : undefined
          }
        >
          <Button onClick={close}>
            <Trans>Done</Trans>
          </Button>
        </ModalFooter>
      </Modal>
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
function KeyRecorder({
  onKey,
  onCancel,
  action,
}: {
  onKey: (combo: string) => void
  /** `returnFocus` is false when focus has already gone somewhere the user chose. */
  onCancel: (returnFocus: boolean) => void
  action: string
}) {
  const { t } = useLingui()
  const ref = useRef<HTMLButtonElement>(null)
  const latest = useRef({ onKey, onCancel })
  latest.current = { onKey, onCancel }

  useEffect(() => {
    ref.current?.focus()
    // The first key pressed is the one recorded, on its own key-up; keys pressed while it is held are ignored.
    let pending: { combo: string; code: string } | null = null
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Tab') {
        latest.current.onCancel(false)
        return
      }
      event.preventDefault()
      event.stopPropagation()
      if (event.key === 'Escape') {
        latest.current.onCancel(true)
        return
      }
      const combo = comboFromEvent(event)
      if (!combo || event.repeat || pending) return
      if (/^(Mod|Ctrl|Meta|Alt)\+/.test(combo)) latest.current.onKey(combo)
      else pending = { combo, code: event.code }
    }
    const onKeyUp = (event: KeyboardEvent) => {
      if (!pending) return
      event.preventDefault()
      event.stopPropagation()
      if (event.code !== pending.code) return
      const { combo } = pending
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
      onBlur={() => latest.current.onCancel(false)}
      onClick={() => latest.current.onCancel(true)}
    >
      <span className="shortcut-recorder-dot" aria-hidden />
      <Trans>Press a key, Esc cancels</Trans>
      <VisuallyHidden>{t({ message: `(${action})`, comment: 'After "Press a key, Esc cancels": the action the key is for' })}</VisuallyHidden>
    </button>
  )
}
