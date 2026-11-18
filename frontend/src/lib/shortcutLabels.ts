import { msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { useLingui } from '@lingui/react'
import { useCallback } from 'react'
import { isMac, MODIFIER_ORDER, parseCombo } from './shortcuts'

const KEY_NAMES: Record<string, MessageDescriptor> = {
  Space: msg`Space`,
  Home: msg`Home`,
  End: msg`End`,
  PageUp: msg`Page Up`,
  PageDown: msg`Page Down`,
  Enter: msg`Enter`,
  Backspace: msg`Backspace`,
  Delete: msg`Delete`,
  Insert: msg`Insert`,
  Escape: msg`Esc`,
  Forward: msg({
    message: 'Forward arrow',
    comment: 'The arrow key that points along the reading direction',
  }),
  Back: msg({
    message: 'Back arrow',
    comment: 'The arrow key that points against the reading direction',
  }),
}

const MODIFIER_NAMES: Record<string, MessageDescriptor> = {
  Ctrl: msg({
    message: 'Ctrl',
    comment: 'The Control key as printed on keyboards in this language',
  }),
  Alt: msg({
    message: 'Alt',
    comment: 'The Alt key as printed on keyboards in this language',
  }),
  Shift: msg({
    message: 'Shift',
    comment: 'The Shift key as printed on keyboards in this language',
  }),
}

const GLYPHS: Record<string, string> = {
  ArrowLeft: '←',
  ArrowRight: '→',
  ArrowUp: '↑',
  ArrowDown: '↓',
  '-': '−',
}

const MAC_MODIFIERS: Record<string, string> = {
  Mod: '⌘',
  Ctrl: '⌃',
  Alt: '⌥',
  Shift: '⇧',
}

/**
 * Words a stored key as the keyboard prints it. With `rtl` given, the reading-direction arrows show
 * as the arrow they stand for; without it (Settings) they keep their names. `compact` drops the plus
 * signs, for a hint inside a button.
 */
export function useFormatCombo(): (combo: string, options?: { rtl?: boolean; compact?: boolean }) => string {
  const { _ } = useLingui()
  return useCallback(
    (combo: string, { rtl, compact = false }: { rtl?: boolean; compact?: boolean } = {}) => {
      const { mods, key } = parseCombo(combo)
      const mac = isMac()
      let keyName: string
      if ((key === 'Forward' || key === 'Back') && rtl !== undefined) {
        keyName = (key === 'Forward') === rtl ? '←' : '→'
      } else if (KEY_NAMES[key]) {
        keyName = _(KEY_NAMES[key])
      } else {
        keyName = GLYPHS[key] ?? (key.length === 1 ? key.toUpperCase() : key)
      }
      const order = MODIFIER_ORDER.filter((mod) => mods.has(mod))
      if (mac)
        return order.map((mod) => MAC_MODIFIERS[mod] ?? mod).join('') + (compact && order.length ? ' ' : '') + keyName
      const names = order.map((mod) => {
        if (mod === 'Mod') return _(MODIFIER_NAMES.Ctrl)
        if (mod === 'Meta') return 'Win'
        return _(MODIFIER_NAMES[mod])
      })
      return [...names, keyName].join(compact ? ' ' : ' + ')
    },
    [_],
  )
}
