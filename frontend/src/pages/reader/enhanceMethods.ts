import { msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import type { EnhanceMethod, ReaderEnhance } from './prefs'

export interface EnhanceMethodInfo {
  id: EnhanceMethod
  label: MessageDescriptor
  hint: MessageDescriptor
}

export const ENHANCE_LEVELS: { value: ReaderEnhance; label: MessageDescriptor }[] = [
  { value: 'off', label: msg`Off` },
  { value: 'auto', label: msg`Auto` },
  { value: 'always', label: msg`Always` },
]

export const BROWSER_ENHANCE_METHODS: EnhanceMethodInfo[] = [
  { id: 'a4k-s', label: msg`Light`, hint: msg`Fastest, on this device` },
  { id: 'a4k-m', label: msg`Balanced`, hint: msg`On this device` },
  {
    id: 'a4k-a',
    label: msg`Strong`,
    hint: msg`On this device; best on clean line art, can mottle screentone`,
  },
]

export const SERVER_ENHANCE_METHODS: EnhanceMethodInfo[] = [
  { id: 'server:span-s', label: msg`Sharp (server)`, hint: msg`IllustrationJaNai SPAN-S` },
  {
    id: 'server:realesrgan-av3',
    label: msg`Crisp line art (server)`,
    hint: msg`Real-ESRGAN; flattens screentone`,
  },
  { id: 'server:waifu2x-art', label: msg`Gentle (server)`, hint: msg`waifu2x art; slowest, cleanest` },
]

export const ENHANCE_METHODS: EnhanceMethodInfo[] = [...BROWSER_ENHANCE_METHODS, ...SERVER_ENHANCE_METHODS]

const SERVER_PREFIX = 'server:'

export function isServerMethod(id: string): boolean {
  return id.startsWith(SERVER_PREFIX)
}

/** The model id the server knows a `server:*` method by, or null for a browser preset. */
export function serverModelId(id: string): string | null {
  return isServerMethod(id) ? id.slice(SERVER_PREFIX.length) : null
}

export function enhanceMethodInfo(id: string): EnhanceMethodInfo | undefined {
  return ENHANCE_METHODS.find((m) => m.id === id)
}
