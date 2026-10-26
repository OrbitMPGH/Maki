import { msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { plural } from '@lingui/core/macro'
import { useLingui } from '@lingui/react/macro'
import type {
  PageConversionFormat,
  PageConversionSample,
  PageConversionSettings,
  PageConversionStop,
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
  compact: msg`Tone thins out.`,
  balanced: msg`Tone is a little lighter. Fine at fit-to-screen.`,
  high: msg`No difference we could see.`,
  lossless: msg`Identical pixels.`,
}

export const FORMAT_LABELS: Record<PageConversionFormat, MessageDescriptor> = {
  webp: msg`WebP`,
  avif: msg`AVIF`,
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
}

export const KEPT_REASONS: Record<string, MessageDescriptor> = {
  notsmaller: msg`Converting would not make this page at least 5% smaller, so the pass keeps it as it is.`,
  toosmall: msg`Pages under 16 KB are always kept as they are.`,
  toolarge: msg`Pages wider or taller than 16383 px are kept as they are.`,
  animated: msg`Animated PNG pages are kept as they are.`,
  highbitdepth: msg`Pages with more than 8 bits per channel are kept as they are.`,
  lossless: msg`JPEG pages are kept as they are at Lossless.`,
  alreadycompact: msg`Pages already stored as WebP or AVIF are kept as they are.`,
  unreadable: msg`This page could not be read, so the pass would leave the file alone.`,
}

export const KEPT_FALLBACK: MessageDescriptor = msg`This page is kept as it is.`

/** "WebP, High", "WebP, High, 2 rules", or "Off". */
export function useSettingsSummary() {
  const { t } = useLingui()
  return (settings: PageConversionSettings, ruleCount = 0): string => {
    if (!settings.format) return t`Off`
    const format = t(FORMAT_LABELS[settings.format])
    const stop = t(STOP_LABELS[settings.stop])
    if (ruleCount === 0) return t`${format}, ${stop}`
    const rules = plural(ruleCount, { one: '# rule', other: '# rules' })
    return t`${format}, ${stop}, ${rules}`
  }
}
