import { i18n } from '@lingui/core'
import { msg } from '@lingui/core/macro'
import type { LocalizedTitle } from './types'

/** Provider codes that are not valid BCP-47 for `Intl.DisplayNames`. */
const CODE_ALIASES: Record<string, string> = { 'es-la': 'es-419' }

const displayNames = new Map<string, Intl.DisplayNames | null>()

function namesFor(locale: string): Intl.DisplayNames | null {
  let names = displayNames.get(locale)
  if (names === undefined) {
    try {
      names = new Intl.DisplayNames([locale || 'en'], { type: 'language' })
    } catch {
      names = null
    }
    displayNames.set(locale, names)
  }
  return names
}

/**
 * The name of a provider language code in the interface language, so an alt-title line reads
 * "ベルセルク (Japanese)" or "ベルセルク (Japonais)" rather than "(ja)". A code the browser cannot
 * name falls back to itself: providers invent regional spellings faster than any list grows.
 */
export function languageName(code: string | null | undefined): string | null {
  if (!code) return null
  const lower = code.toLowerCase()
  if (lower === 'romaji') return i18n._(msg`Romanized`)
  try {
    return namesFor(i18n.locale)?.of(CODE_ALIASES[lower] ?? code) ?? code
  } catch {
    return code
  }
}

/** `"ベルセルク (Japanese)"`, or the bare title when the provider gave no language for it. */
export function altTitleLabel(alt: LocalizedTitle): string {
  const name = languageName(alt.language)
  return name ? `${alt.title} (${name})` : alt.title
}

/**
 * The alt titles worth showing beside `displayed`: the original-script title first, then everything
 * the provider tagged, with anything already on screen dropped. Duplicates are common, the English
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

/** The part before the first hyphen, lowercased: "pt-BR" -> "pt". */
function primarySubtag(code: string): string {
  return code.split('-')[0].toLowerCase()
}

/** BCP-47 spells a script subtag with four letters, a region with two letters or three digits. */
function scriptSubtag(code: string): string | null {
  const parts = code.split('-').slice(1)
  const script = parts.find((p) => /^[A-Za-z]{4}$/.test(p))
  return script ? script.toLowerCase() : null
}

/**
 * Whether `language` is the same written language as `wanted`, ignoring region. A script subtag is
 * not ignored: MangaBaka tags romanizations "ja-Latn", and someone reading the UI in Japanese wants
 * カッコいい女の子, not "Kakkoi Onnanoko". Mirrors `LocalizedTitle.Matches` on the backend, except
 * that this one is symmetric about region, because the UI locale carries one ("pt-BR") and the
 * titles often don't.
 */
function sameLanguage(language: string, wanted: string): boolean {
  if (language.toLowerCase() === wanted.toLowerCase()) return true
  if (primarySubtag(language) !== primarySubtag(wanted)) return false

  const script = scriptSubtag(language)
  return script === null || script === scriptSubtag(wanted)
}

/**
 * The alt titles worth putting in front of a reader: English, plus their interface language when
 * that isn't English. Providers tag dozens per series and a Vietnamese spelling of a title is noise
 * to everyone who doesn't read Vietnamese.
 *
 * Untagged titles survive. MangaBaka leaves English respellings without a language, so dropping them
 * would blank the line on series whose only alt titles are exactly the ones an English reader wants.
 */
export function readableTitles(alt: LocalizedTitle[], locale: string): LocalizedTitle[] {
  return alt.filter(
    (t) => !t.language || sameLanguage(t.language, 'en') || sameLanguage(t.language, locale),
  )
}
