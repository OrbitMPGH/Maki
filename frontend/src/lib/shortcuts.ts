import { msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { useCallback, useEffect, useMemo, useRef } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, currentSession } from '../api/client'

/**
 * Every rebindable key in the app, its default keys, and how a key press becomes a stored string.
 *
 * A key is stored as modifiers plus `KeyboardEvent.key` (`Shift+Space`, `Mod+k`, `Shift+j`, `[`), so
 * it follows the keyboard layout: `?` is whatever key types a question mark. A punctuation character
 * carries no Shift, since the character already says it (`?`, not `Shift+/`). Letters are stored
 * lower case with Shift when it was held, so Caps Lock changes nothing. `Mod` is Ctrl, or Cmd on a Mac.
 *
 * `Forward` and `Back` are the arrow that points along the reading direction and the one against it,
 * so page turning keeps working when a series switches direction. A plain left or right arrow
 * recorded on Next or Previous page is stored as one of them, and while either is bound no other
 * action may take a plain left or right arrow, since in one direction or the other it would be the
 * same key.
 */

export const isMac = () =>
  typeof navigator !== 'undefined' && /Mac|iPhone|iPad|iPod/i.test(navigator.platform || navigator.userAgent)

export type ShortcutGroup = 'pages' | 'autoScroll' | 'layout' | 'other' | 'app'

export interface ShortcutAction {
  id: string
  group: ShortcutGroup
  label: MessageDescriptor
  keys: string[]
}

export const SHORTCUT_ACTIONS = [
  { id: 'reader.next', group: 'pages', label: msg`Next page`, keys: ['Forward'] },
  { id: 'reader.previous', group: 'pages', label: msg`Previous page`, keys: ['Back'] },
  { id: 'reader.screenDown', group: 'pages', label: msg`Scroll down or next page`, keys: ['Space', 'PageDown'] },
  { id: 'reader.screenUp', group: 'pages', label: msg`Scroll up or previous page`, keys: ['Shift+Space', 'PageUp'] },
  { id: 'reader.first', group: 'pages', label: msg`First page`, keys: ['Home'] },
  { id: 'reader.last', group: 'pages', label: msg`Last page`, keys: ['End'] },
  { id: 'reader.autoScroll', group: 'autoScroll', label: msg`Auto-scroll on or off`, keys: ['a'] },
  { id: 'reader.autoScrollSlower', group: 'autoScroll', label: msg`Auto-scroll slower`, keys: ['['] },
  { id: 'reader.autoScrollFaster', group: 'autoScroll', label: msg`Auto-scroll faster`, keys: [']'] },
  { id: 'reader.single', group: 'layout', label: msg`Single page`, keys: ['1'] },
  { id: 'reader.double', group: 'layout', label: msg`Double page`, keys: ['2'] },
  { id: 'reader.continuous', group: 'layout', label: msg`Continuous`, keys: ['3'] },
  { id: 'reader.direction', group: 'layout', label: msg`Switch reading direction`, keys: ['d'] },
  { id: 'reader.crop', group: 'layout', label: msg`Crop borders on or off`, keys: ['c'] },
  { id: 'reader.enhance', group: 'layout', label: msg`Always enhance on or off`, keys: ['e'] },
  { id: 'reader.zoomIn', group: 'layout', label: msg`Zoom in`, keys: ['+', '='] },
  { id: 'reader.zoomOut', group: 'layout', label: msg`Zoom out`, keys: ['-'] },
  { id: 'reader.zoomReset', group: 'layout', label: msg`Reset zoom`, keys: ['0'] },
  { id: 'reader.fullscreen', group: 'other', label: msg`Full screen`, keys: ['f'] },
  { id: 'reader.thumbnails', group: 'other', label: msg`Page thumbnails`, keys: ['t'] },
  { id: 'reader.bookmark', group: 'other', label: msg`Bookmark this page`, keys: ['b'] },
  { id: 'reader.previousBookmark', group: 'other', label: msg`Previous bookmark`, keys: [',', '<'] },
  { id: 'reader.nextBookmark', group: 'other', label: msg`Next bookmark`, keys: ['.', '>'] },
  { id: 'reader.compare', group: 'other', label: msg`Hold to see the original page`, keys: ['h'] },
  { id: 'reader.shortcuts', group: 'other', label: msg`Show or hide keyboard shortcuts`, keys: ['?'] },
  // On a Mac, Ctrl+K opened search too before shortcuts could be changed.
  { id: 'app.search', group: 'app', label: msg`Open search`, keys: isMac() ? ['Mod+k', 'Ctrl+k'] : ['Mod+k'] },
] as const satisfies readonly ShortcutAction[]

export type ShortcutActionId = (typeof SHORTCUT_ACTIONS)[number]['id']

export const SHORTCUT_GROUPS: ShortcutGroup[] = ['pages', 'autoScroll', 'layout', 'other', 'app']

export const SHORTCUT_GROUP_LABELS: Record<ShortcutGroup, MessageDescriptor> = {
  pages: msg`Turning pages`,
  autoScroll: msg`Auto-scroll`,
  layout: msg`Layout`,
  other: msg`Everything else`,
  app: msg`Outside the reader`,
}

export function actionLabel(id: ShortcutActionId): MessageDescriptor {
  return SHORTCUT_ACTIONS.find((action) => action.id === id)!.label
}

/** The most keys one action takes. The server allows a few more. */
export const MAX_KEYS_PER_ACTION = 3

const MODIFIER_KEYS = new Set(['Shift', 'Control', 'Alt', 'AltGraph', 'Meta', 'OS', 'CapsLock', 'NumLock', 'Fn', 'Hyper', 'Super'])
export const MODIFIER_ORDER = ['Mod', 'Ctrl', 'Meta', 'Alt', 'Shift'] as const
type Modifier = (typeof MODIFIER_ORDER)[number]

export type ShortcutBindings = Record<ShortcutActionId, string[]>
export type ShortcutOverrides = Partial<Record<ShortcutActionId, string[]>>

const DEFAULTS = Object.fromEntries(SHORTCUT_ACTIONS.map((action) => [action.id, [...action.keys]])) as ShortcutBindings

export function defaultKeys(id: ShortcutActionId): string[] {
  return DEFAULTS[id]
}


/** A stored key split into its modifiers and the key itself. `Mod++` is Ctrl and the plus key. */
export function parseCombo(combo: string): { mods: Set<Modifier>; key: string } {
  const mods = new Set<Modifier>()
  let rest = combo
  for (;;) {
    const match = /^(Mod|Ctrl|Meta|Alt|Shift)\+(.+)$/.exec(rest)
    if (!match) break
    mods.add(match[1] as Modifier)
    rest = match[2]
  }
  return { mods, key: rest }
}

function joinCombo(mods: Set<Modifier>, key: string): string {
  return [...MODIFIER_ORDER.filter((mod) => mods.has(mod)), key].join('+')
}

const isLetter = (key: string) => key.length === 1 && key.toLowerCase() !== key.toUpperCase()

/** The key part of an event, as stored: `Space` for the space bar, letters lower-cased. */
function eventKey(event: KeyboardEvent): string | null {
  const key = event.key
  if (!key || key === 'Dead' || key === 'Unidentified' || MODIFIER_KEYS.has(key)) return null
  if (key === ' ') return 'Space'
  return key.length === 1 ? key.toLowerCase() : key
}

/** The stored form of a key press, or null for a lone modifier or a dead key. */
export function comboFromEvent(event: KeyboardEvent): string | null {
  const key = eventKey(event)
  if (!key) return null
  const mods = new Set<Modifier>()
  // AltGr reports as Ctrl+Alt on Windows; it is how many layouts type [ ] and @, not a shortcut.
  const altGraph = event.getModifierState?.('AltGraph') ?? false
  const mac = isMac()
  if (!altGraph) {
    if (mac ? event.metaKey : event.ctrlKey) mods.add('Mod')
    if (mac ? event.ctrlKey : event.metaKey) mods.add(mac ? 'Ctrl' : 'Meta')
    if (event.altKey) mods.add('Alt')
  }
  if (event.shiftKey && (key.length > 1 || isLetter(key))) mods.add('Shift')
  return joinCombo(mods, key)
}

/** Whether a combo holds Ctrl, Cmd or (off a Mac, where Option types characters) Alt: a key that types nothing. */
export function hasCommandModifier(combo: string): boolean {
  const { mods } = parseCombo(combo)
  return mods.has('Mod') || mods.has('Ctrl') || mods.has('Meta') || (mods.has('Alt') && !isMac())
}

const SIDE_ARROWS = new Set(['ArrowLeft', 'ArrowRight'])
const READING_ARROWS = new Set(['Forward', 'Back'])

/** Whether a combo is a plain left or right arrow, which Next and Previous page claim. */
export function isSideArrow(combo: string): boolean {
  return SIDE_ARROWS.has(parseCombo(combo).key)
}

export function isReadingArrow(combo: string): boolean {
  return READING_ARROWS.has(parseCombo(combo).key)
}

/** How a recorded key is stored on an action: a side arrow on Next or Previous page follows the reading direction. */
export function comboForAction(id: ShortcutActionId, combo: string): string {
  if (id !== 'reader.next' && id !== 'reader.previous') return combo
  const { mods, key } = parseCombo(combo)
  if (!SIDE_ARROWS.has(key)) return combo
  return joinCombo(mods, id === 'reader.next' ? 'Forward' : 'Back')
}

/** The stored keys that answer to the same press as `combo` in at least one reading direction. */
function clashesWith(combo: string): string[] {
  const { mods, key } = parseCombo(combo)
  if (SIDE_ARROWS.has(key)) return [combo, joinCombo(mods, 'Forward'), joinCombo(mods, 'Back')]
  if (READING_ARROWS.has(key)) return [combo, joinCombo(mods, 'ArrowLeft'), joinCombo(mods, 'ArrowRight')]
  return [combo]
}

export interface ShortcutHolder {
  id: ShortcutActionId
  combo: string
}

/** Every key on another action that the same press as `combo` would also trigger. */
export function holdersOf(bindings: ShortcutBindings, combo: string, except: ShortcutActionId): ShortcutHolder[] {
  const clashes = clashesWith(combo)
  return SHORTCUT_ACTIONS.flatMap((action) =>
    action.id === except
      ? []
      : bindings[action.id].filter((held) => clashes.includes(held)).map((held) => ({ id: action.id, combo: held })),
  )
}

/**
 * Gives `id` these keys and takes any clashing key off every other action. Returns the keys it
 * took, for telling the user.
 */
export function assignKeys(
  bindings: ShortcutBindings,
  id: ShortcutActionId,
  keys: string[],
): { next: ShortcutBindings; moved: ShortcutHolder[] } {
  const next = { ...bindings, [id]: keys }
  const moved = keys.flatMap((combo) => holdersOf(next, combo, id))
  for (const { id: holder, combo } of moved) next[holder] = next[holder].filter((key) => key !== combo)
  return { next, moved }
}

/** The bindings with the saved overrides applied. Unknown ids in the saved set are ignored. */
export function resolveBindings(overrides: ShortcutOverrides | undefined): ShortcutBindings {
  const resolved = { ...DEFAULTS }
  if (!overrides) return resolved
  for (const action of SHORTCUT_ACTIONS) {
    const keys = overrides[action.id]
    if (Array.isArray(keys)) resolved[action.id] = keys
  }
  return resolved
}

/** What to save: only the actions that differ from their defaults. */
export function toOverrides(bindings: ShortcutBindings): ShortcutOverrides {
  const overrides: ShortcutOverrides = {}
  for (const action of SHORTCUT_ACTIONS) {
    const keys = bindings[action.id]
    const defaults = DEFAULTS[action.id]
    if (keys.length !== defaults.length || keys.some((key, i) => key !== defaults[i])) overrides[action.id] = keys
  }
  return overrides
}

/**
 * The action a press runs. Shift on a named key (an arrow, Page Down, Home) that has no binding of
 * its own falls back to the key without it, as the reader always treated those keys.
 */
export function actionFor(map: Map<string, ShortcutActionId>, combo: string): ShortcutActionId | undefined {
  const action = map.get(combo)
  if (action) return action
  const { mods, key } = parseCombo(combo)
  if (!mods.has('Shift') || key.length === 1) return undefined
  mods.delete('Shift')
  return map.get(joinCombo(mods, key))
}

/** A lookup from key to action for one reading direction. */
export function buildKeyMap(bindings: ShortcutBindings, rtl: boolean): Map<string, ShortcutActionId> {
  const map = new Map<string, ShortcutActionId>()
  for (const action of SHORTCUT_ACTIONS) {
    for (const combo of bindings[action.id]) {
      const { mods, key } = parseCombo(combo)
      const pressed = READING_ARROWS.has(key)
        ? joinCombo(mods, (key === 'Forward') === rtl ? 'ArrowLeft' : 'ArrowRight')
        : combo
      if (!map.has(pressed)) map.set(pressed, action.id)
    }
  }
  return map
}

/** `aria-keyshortcuts` wants `Control+K Meta+K`, not the stored form: every binding, plus spelled out. */
export function ariaKeyShortcuts(combos: string[]): string {
  return combos
    .filter((combo) => !READING_ARROWS.has(parseCombo(combo).key))
    .map((combo) => {
      const { mods, key } = parseCombo(combo)
      const names: string[] = []
      if (mods.has('Mod')) names.push(isMac() ? 'Meta' : 'Control')
      if (mods.has('Ctrl')) names.push('Control')
      if (mods.has('Meta')) names.push('Meta')
      if (mods.has('Alt')) names.push('Alt')
      if (mods.has('Shift')) names.push('Shift')
      names.push(key === '+' ? 'Plus' : key === 'Space' ? 'Space' : key.length === 1 ? key.toUpperCase() : key)
      return names.join('+')
    })
    .join(' ')
}

interface ShortcutSettings {
  bindings: ShortcutOverrides
}

const SHORTCUTS_KEY = ['settings', 'shortcuts']

export function useShortcutSettings() {
  return useQuery({
    queryKey: SHORTCUTS_KEY,
    queryFn: () => api<ShortcutSettings>('/settings/shortcuts'),
    staleTime: Infinity,
  })
}

const SAVE_SCOPE = 'settings/shortcuts'

/**
 * Saves run one at a time and each shows at once. A save's answer is only applied when no later
 * save is queued, since it would show the state before that save, and a failure refetches what the
 * server holds once the queue is empty. A save queued in one session never runs in the next: after
 * a sign-out it would write the old account's keys into whoever signed in.
 */
export function useSaveShortcutSettings() {
  const queryClient = useQueryClient()
  const othersPending = () => queryClient.isMutating({ predicate: (m) => m.options.scope?.id === SAVE_SCOPE }) > 1
  const mutation = useMutation({
    scope: { id: SAVE_SCOPE },
    mutationFn: async ({ bindings, session }: { bindings: ShortcutOverrides; session: number }) => {
      if (session !== currentSession()) throw new Error('The session that made this change has ended')
      return api<ShortcutSettings>('/settings/shortcuts', { method: 'PUT', body: JSON.stringify({ bindings }) })
    },
    onMutate: async ({ bindings }) => {
      await queryClient.cancelQueries({ queryKey: SHORTCUTS_KEY })
      queryClient.setQueryData<ShortcutSettings>(SHORTCUTS_KEY, { bindings })
    },
    onError: (_error, { session }) => {
      if (session === currentSession() && !othersPending()) void queryClient.invalidateQueries({ queryKey: SHORTCUTS_KEY })
    },
    onSuccess: (saved, { session }) => {
      if (session === currentSession() && !othersPending()) queryClient.setQueryData(SHORTCUTS_KEY, saved)
    },
  })
  const { mutate } = mutation
  return useCallback(
    (bindings: ShortcutOverrides, { onError }: { onError?: () => void } = {}) => {
      const session = currentSession()
      mutate({ bindings, session }, { onError: () => session === currentSession() && onError?.() })
    },
    [mutate],
  )
}

/** The caller's bindings. Until the saved set arrives, and when it can't be fetched, the defaults. */
export function useShortcutBindings(): ShortcutBindings {
  const { data } = useShortcutSettings()
  return useMemo(() => resolveBindings(data?.bindings), [data])
}

function isTyping(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false
  return ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName) || target.isContentEditable
}

const CONTROLS = 'button, a, [role="button"], [role="slider"], [role="menuitem"], [role="option"], [role="tab"], summary'

/** Space or Enter on a focused control presses that control, whatever is bound to them. */
export function pressesControl(target: EventTarget | null, combo: string): boolean {
  const { key } = parseCombo(combo)
  if (key !== 'Space' && key !== 'Enter') return false
  return target instanceof HTMLElement && Boolean(target.closest(CONTROLS))
}

/**
 * Runs `handler` on the action's keys anywhere on the page. A field keeps its keys, except that with
 * `inFields` a key held with Ctrl, Cmd or Alt still runs there: it types nothing, and people reach
 * for search while typing.
 */
export function useShortcut(id: ShortcutActionId, handler: () => void, { inFields = false } = {}) {
  const keys = useShortcutBindings()[id]
  const latest = useRef(handler)
  latest.current = handler
  useEffect(() => {
    if (keys.length === 0) return
    const onKey = (event: KeyboardEvent) => {
      if (event.defaultPrevented || event.repeat) return
      const combo = comboFromEvent(event)
      if (!combo || !keys.includes(combo)) return
      if (isTyping(event.target) && !(inFields && hasCommandModifier(combo))) return
      if (pressesControl(event.target, combo)) return
      event.preventDefault()
      latest.current()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [inFields, keys])
}
