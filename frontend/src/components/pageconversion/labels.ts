import { msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { plural } from '@lingui/core/macro'
import { useLingui } from '@lingui/react/macro'
import {
  colourTargetOf,
  type PageConversionFormat,
  type PageConversionSample,
  type PageConversionSettings,
  type PageConversionStop,
  type PageTarget,
} from '../../api/pageConversion'

export const STOP_LABELS: Record<PageConversionStop, MessageDescriptor> = {
  smallest: msg`Smallest`,
  compact: msg`Compact`,
  balanced: msg`Balanced`,
  high: msg`High`,
  lossless: msg`Lossless`,
}

/** What each stop does to a page viewed at 1:1, measured on screentone and thin lines. */
export const STOP_HINTS: Record<PageConversionStop, MessageDescriptor> = {
  smallest: msg`Screentone is smoothed away, lines stay intact.`,
  compact: msg`Screentone gets lighter but stays visible.`,
  balanced: msg`Tone is a little lighter. Fine at fit-to-screen.`,
  high: msg`No difference we could see.`,
  lossless: msg`Identical pixels.`,
}

/** The same, for colour pages, where there is no screentone and gradients and skin show the loss first. */
export const COLOUR_STOP_HINTS: Record<PageConversionStop, MessageDescriptor> = {
  smallest: msg`The most softening. Check skies, skin and dark gradients.`,
  compact: msg`Fine detail gets softer. Check dark gradients.`,
  balanced: msg`A little softening in fine detail. Fine at fit-to-screen.`,
  high: msg`Hard to tell from the original.`,
  lossless: msg`Identical pixels.`,
}

export const FORMAT_LABELS: Record<PageConversionFormat, MessageDescriptor> = {
  webp: msg`WebP`,
  avif: msg`AVIF`,
  jxl: msg`JPEG XL`,
}

/** Every format a target can take, in the order the controls list them. */
export const CONVERSION_FORMATS: readonly PageConversionFormat[] = ['webp', 'jxl', 'avif']

export const FORMAT_HINTS: Record<PageConversionFormat, MessageDescriptor> = {
  webp: msg`Opens in every reader Maki works with.`,
  avif: msg`The smallest files. KOReader and some e-readers can't open AVIF pages.`,
  jxl: msg`About WebP's size, a little smaller at High, and much faster to convert. Readers that can't show JPEG XL get WebP made on the fly, but a CBZ file you download keeps its JPEG XL pages, which KOReader and most comic apps can't open.`,
}

/** The page formats a rule can name. Wire values, so lowercase. */
export const RULE_PAGE_FORMATS = ['jpg', 'png', 'mixed'] as const

export const PAGE_FORMAT_LABELS: Record<(typeof RULE_PAGE_FORMATS)[number], MessageDescriptor> = {
  jpg: msg`JPEG`,
  png: msg`PNG`,
  mixed: msg`Mixed JPEG and PNG`,
}

export const SAMPLE_REASONS: Record<PageConversionSample['reason'], MessageDescriptor> = {
  greyscale: msg`Greyscale page`,
  colour: msg`Colour page`,
  heaviest: msg`Heaviest kind of file`,
  custom: msg`Your pick`,
  tall: msg`Tallest page`,
}

export const KEPT_REASONS: Record<string, MessageDescriptor> = {
  notsmaller: msg`Converting would not make this page at least 5% smaller, so the pass keeps it as it is.`,
  toosmall: msg`Pages under 16 KB are always kept as they are.`,
  toolarge: msg`Pages wider or taller than 16383 px are kept as they are.`,
  animated: msg`Animated PNG pages are kept as they are.`,
  highbitdepth: msg`Pages with more than 8 bits per channel are kept as they are.`,
  lossless: msg`JPEG pages are kept as they are at Lossless.`,
  alreadycompact: msg`Pages already stored as WebP, AVIF or JPEG XL are kept as they are.`,
  unreadable: msg`This page could not be read, so the pass would leave the file alone.`,
}

export const KEPT_FALLBACK: MessageDescriptor = msg`This page is kept as it is.`

/** "WebP, High", or "Lossless", which has no format. */
export function useTargetLabel() {
  const { t } = useLingui()
  return (target: PageTarget): string => {
    const stop = t(STOP_LABELS[target.stop])
    if (target.stop === 'lossless') return stop
    const format = t(FORMAT_LABELS[target.format])
    return t`${format}, ${stop}`
  }
}

/**
 * "WebP, High" when colour pages convert like black-and-white ones, else
 * "WebP, High for black-and-white pages, AVIF, Balanced for colour pages".
 */
export function useTargetsLabel() {
  const { t } = useLingui()
  const label = useTargetLabel()
  return (grey: PageTarget, colour: PageTarget | null): string => {
    const greyLabel = label(grey)
    if (!colour) return greyLabel
    const colourLabel = label(colour)
    return t`${greyLabel} for black-and-white pages, ${colourLabel} for colour pages`
  }
}

/** The default targets of a plan, or null while conversion is off. */
export function defaultTargets(settings: PageConversionSettings): { grey: PageTarget; colour: PageTarget | null } | null {
  if (!settings.format) return null
  return { grey: { format: settings.format, stop: settings.stop }, colour: colourTargetOf(settings) }
}

/** "WebP, High", "WebP, High, 2 rules", or "Off". */
export function useSettingsSummary() {
  const { t } = useLingui()
  const targetsLabel = useTargetsLabel()
  return (settings: PageConversionSettings, ruleCount = 0): string => {
    const targets = defaultTargets(settings)
    if (!targets) return t`Off`
    const label = targetsLabel(targets.grey, targets.colour)
    if (ruleCount === 0) return label
    const rules = plural(ruleCount, { one: '# rule', other: '# rules' })
    return t`${label}, ${rules}`
  }
}

const CHECKED_WITH_NAMES: Record<string, MessageDescriptor> = {
  lossless: msg`Lossless`,
  skip: msg`Checked, not converted`,
  'skip:hard': msg`Checked, cannot be converted`,
  undone: msg`Restored from the original`,
  unknown: msg`Settings not recorded`,
}

const TARGET_NAME = '(?:webp|avif|jxl):(?:smallest|compact|balanced|high)|lossless'
const CHECKED_WITH_PATTERN = new RegExp(`^(${TARGET_NAME})(?:/(${TARGET_NAME}))?(\\+png)?$`)

function parseTargetName(name: string): PageTarget {
  if (name === 'lossless') return { format: 'webp', stop: 'lossless' }
  const [format, stop] = name.split(':')
  return { format: format as PageConversionFormat, stop: stop as PageConversionStop }
}

/**
 * Names the settings a file was checked or converted with, from the wire name the server stamps:
 * "webp:high" is "WebP, High", "avif:balanced+png" is "AVIF, Balanced, PNG lossless", and
 * "webp:high/avif:balanced" names the colour pages' target after the slash. A name this client does
 * not know comes back as it arrived.
 */
export function useCheckedWithLabel() {
  const { t } = useLingui()
  const targetsLabel = useTargetsLabel()
  return (name: string | null | undefined): string => {
    if (!name) return t(CHECKED_WITH_NAMES.unknown)
    const fixed = CHECKED_WITH_NAMES[name]
    if (fixed) return t(fixed)
    const match = CHECKED_WITH_PATTERN.exec(name)
    if (!match) return name
    const label = targetsLabel(parseTargetName(match[1]), match[2] ? parseTargetName(match[2]) : null)
    return match[3] ? t`${label}, PNG lossless` : label
  }
}
