import type { LocalizedTitle } from './types'

/**
 * Display names for the language codes metadata providers actually use, so an alt-title line reads
 * "ベルセルク (Japanese)" rather than "(ja)". A code that isn't here falls back to itself — the list
 * is a courtesy, not a whitelist, and providers invent regional spellings faster than this grows.
 */
const LANGUAGE_NAMES: Record<string, string> = {
  en: 'English',
  sv: 'Swedish',
  nl: 'Dutch',
  ja: 'Japanese',
  ko: 'Korean',
  zh: 'Chinese',
  'zh-hans': 'Chinese (Simplified)',
  'zh-hant': 'Chinese (Traditional)',
  'zh-hk': 'Chinese (HK)',
  es: 'Spanish',
  'es-la': 'Spanish (LATAM)',
  fr: 'French',
  de: 'German',
  it: 'Italian',
  pt: 'Portuguese',
  'pt-br': 'Portuguese (Br)',
  ru: 'Russian',
  ar: 'Arabic',
  id: 'Indonesian',
  th: 'Thai',
  vi: 'Vietnamese',
  pl: 'Polish',
  tr: 'Turkish',
  'ja-latn': 'Romanized Japanese',
  'ko-latn': 'Romanized Korean',
  'zh-latn': 'Romanized Chinese',
  romaji: 'Romanized',
}

export function languageName(code: string | null | undefined): string | null {
  if (!code) return null
  return LANGUAGE_NAMES[code.toLowerCase()] ?? code
}

/** `"ベルセルク (Japanese)"`, or the bare title when the provider gave no language for it. */
export function altTitleLabel(alt: LocalizedTitle): string {
  const name = languageName(alt.language)
  return name ? `${alt.title} (${name})` : alt.title
}

/**
 * The alt titles worth showing beside `displayed`: the original-script title first, then everything
 * the provider tagged, with anything already on screen dropped. Duplicates are common — the English
 * alt title of an English-titled series is the title again.
 */
export function otherTitles(
  alt: LocalizedTitle[],
  originalTitle: string | null,
  ...displayed: (string | null)[]
): LocalizedTitle[] {
  const seen = new Set(displayed.filter(Boolean) as string[])
  const out: LocalizedTitle[] = []

  for (const candidate of [
    ...(originalTitle ? [{ title: originalTitle, language: null }] : []),
    ...alt,
  ]) {
    if (seen.has(candidate.title)) continue
    seen.add(candidate.title)
    out.push(candidate)
  }

  return out
}
