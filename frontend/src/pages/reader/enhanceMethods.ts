import { msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { DEFAULT_ENHANCE_METHOD, type EnhanceFallback, type EnhanceMethod, type EnhanceProgress, type ReaderEnhance } from './prefs'
import { QUALITY_TIERS, QUALITY_TIER_LABELS, type QualityTierName } from '../../api/upgrades'

/** 1 to 5, for the settings page's markers. */
export type ModelRating = 1 | 2 | 3 | 4 | 5

export interface EnhanceMethodInfo {
  id: EnhanceMethod
  label: MessageDescriptor
  hint: MessageDescriptor
  /**
   * Server models only. Speed follows the time per page on the server's CPU (Maki 2x 1x, SPAN-S 2x, Real-ESRGAN
   * 3x); quality is the owner's ranking by eye, Real-ESRGAN above Maki 2x.
   */
  speed?: ModelRating
  quality?: ModelRating
  recommended?: boolean
}

export const ENHANCE_LEVELS: { value: ReaderEnhance; label: MessageDescriptor }[] = [
  { value: 'off', label: msg`Off` },
  { value: 'auto', label: msg`Auto` },
  { value: 'always', label: msg`Always` },
]

export const BROWSER_ENHANCE_METHODS: EnhanceMethodInfo[] = [
  { id: 'a4k-s', label: msg`Light`, hint: msg`Fastest` },
  { id: 'a4k-m', label: msg`Balanced`, hint: msg`Between Light and Strong` },
  {
    id: 'a4k-a',
    label: msg`Strong`,
    hint: msg`Best on clean line art; can mottle screentone`,
  },
]

export const SERVER_ENHANCE_METHODS: EnhanceMethodInfo[] = [
  {
    id: 'server:maki-2x',
    label: msg`Maki 2x`,
    hint: msg`Maki's own model; looks like Real-ESRGAN and runs faster`,
    speed: 5,
    quality: 4,
    recommended: true,
  },
  { id: 'server:span-s', label: msg`SPAN-S`, hint: msg`IllustrationJaNai's sharp model`, speed: 3, quality: 3 },
  {
    id: 'server:realesrgan-av3',
    label: msg`Real-ESRGAN`,
    hint: msg`The crispest line art; flattens screentone`,
    speed: 2,
    quality: 5,
  },
]

export const ENHANCE_FALLBACKS: { value: EnhanceFallback; label: MessageDescriptor }[] = [
  ...BROWSER_ENHANCE_METHODS.map((m) => ({ value: m.id as EnhanceFallback, label: m.label })),
  { value: 'off', label: msg`None` },
]

export const ENHANCE_PROGRESS_STYLES: { value: EnhanceProgress; label: MessageDescriptor }[] = [
  { value: 'line', label: msg`Line` },
  { value: 'detailed', label: msg`Detailed` },
  { value: 'off', label: msg`Hidden` },
]

/** Best first, for the "Don't enhance" picker. */
export const ENHANCE_SKIP_TIERS: { value: QualityTierName; label: MessageDescriptor }[] = [...QUALITY_TIERS]
  .reverse()
  .map((tier) => ({ value: tier, label: QUALITY_TIER_LABELS[tier] }))

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

/**
 * The method a picker shows. The default server method reads as its fallback where this server does not
 * offer it, since that is what the reader gets; a server method picked by hand stays visible as chosen.
 */
export function shownEnhanceMethod(method: EnhanceMethod, fallback: EnhanceFallback, offered: boolean): EnhanceMethod {
  return method === DEFAULT_ENHANCE_METHOD && !offered && fallback !== 'off' ? fallback : method
}
