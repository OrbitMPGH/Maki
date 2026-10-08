import type { KeyboardEvent, MouseEvent } from 'react'

type PressEvent = MouseEvent<HTMLElement> | KeyboardEvent<HTMLElement>

/** Enter and Space on the element itself; keys from controls inside it keep their own meaning. */
export function onPressKey(onPress: (event: KeyboardEvent<HTMLElement>) => void) {
  return (event: KeyboardEvent<HTMLElement>) => {
    if (event.target !== event.currentTarget) return
    if (event.key !== 'Enter' && event.key !== ' ') return
    event.preventDefault()
    onPress(event)
  }
}

/** Props that make a non-button element (Card, Badge) focusable and operable from the keyboard. */
export function pressable(onPress: (event: PressEvent) => void) {
  return {
    role: 'button' as const,
    tabIndex: 0,
    onClick: onPress,
    onKeyDown: onPressKey(onPress),
  }
}
