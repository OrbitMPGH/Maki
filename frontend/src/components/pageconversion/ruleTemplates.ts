import { msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import {
  colourTargetOf,
  type PageConversionConditions,
  type PageConversionRule,
  type PageConversionSettings,
  type PageConversionStop,
} from '../../api/pageConversion'
import { randomUUID } from '../../lib/uuid'

/** A rule in the wizard, with an identifier that stays put while the list is reordered. */
export type RuleDraft = PageConversionRule & { key: string }

export const BYTES_PER_MB = 1024 * 1024

/**
 * A new rule. A convert rule starts in the plan's default formats, with colour pages following the
 * default colour target when there is one.
 */
export function newRuleDraft(
  rule: Partial<PageConversionRule> & { stop: PageConversionStop | null },
  defaults?: PageConversionSettings,
): RuleDraft {
  const colour = defaults ? colourTargetOf(defaults) : null
  const convert = rule.stop !== null
  return {
    name: '',
    conditions: {},
    action: convert ? 'convert' : 'skip',
    ...(convert && defaults?.format ? { format: defaults.format } : {}),
    ...(convert && colour
      ? { colourFormat: colour.stop === 'lossless' ? null : colour.format, colourStop: colour.stop === 'lossless' ? 'lossless' : rule.stop }
      : {}),
    ...rule,
    key: randomUUID(),
  }
}

export function toRuleDraft(rule: PageConversionRule): RuleDraft {
  return { ...rule, key: rule.id != null ? `rule-${rule.id}` : randomUUID() }
}

const positive = (n: number | undefined) => (typeof n === 'number' && Number.isFinite(n) && n > 0 ? n : undefined)

/** Drops every condition that has no value, so the server never sees an empty list or a zero. */
export function cleanConditions(c: PageConversionConditions): PageConversionConditions {
  const list = <T extends string>(values: T[] | undefined) => {
    const kept = (values ?? []).map((v) => v.trim()).filter(Boolean) as T[]
    return kept.length > 0 ? kept : undefined
  }
  const out: PageConversionConditions = {
    tiers: list(c.tiers),
    sources: list(c.sources),
    formats: list(c.formats),
    seriesTypes: list(c.seriesTypes),
    tags: list(c.tags),
    bppUnder: positive(c.bppUnder),
    bppOver: positive(c.bppOver),
    sizeOverBytes: positive(c.sizeOverBytes),
    sizeUnderBytes: positive(c.sizeUnderBytes),
  }
  return Object.fromEntries(Object.entries(out).filter(([, v]) => v !== undefined))
}

export function hasConditions(c: PageConversionConditions): boolean {
  return Object.keys(cleanConditions(c)).length > 0
}

/**
 * What goes over the wire: no client keys, no empty conditions, and targets only on a convert rule.
 * A colour target that converts like the black-and-white one is sent as none.
 */
export function toRuleDtos(drafts: readonly PageConversionRule[]): PageConversionRule[] {
  return drafts.map((r) => {
    const convert = r.action === 'convert'
    const colour = convert ? colourTargetOf(r) : null
    return {
      ...(r.id != null ? { id: r.id } : {}),
      name: r.name.trim(),
      conditions: cleanConditions(r.conditions),
      action: r.action,
      stop: convert ? r.stop : null,
      format: convert && r.stop !== 'lossless' ? (r.format ?? null) : null,
      colourFormat: colour && colour.stop !== 'lossless' ? colour.format : null,
      colourStop: colour?.stop ?? null,
    }
  })
}

/** Whether two lists would save the same thing. Ids are ignored: the server hands them out. */
export function sameRules(a: readonly PageConversionRule[], b: readonly PageConversionRule[]): boolean {
  const strip = (rules: readonly PageConversionRule[]) =>
    JSON.stringify(
      toRuleDtos(rules).map((r) => [r.name, r.conditions, r.action, r.stop, r.format, r.colourFormat, r.colourStop]),
    )
  return strip(a) === strip(b)
}

export interface RuleTemplate {
  id: string
  title: MessageDescriptor
  hint: MessageDescriptor
  rule: Pick<PageConversionRule, 'conditions' | 'action' | 'stop'>
}

export const RULE_TEMPLATES: readonly RuleTemplate[] = [
  {
    id: 'scraped',
    title: msg`Leave scraped chapters alone`,
    hint: msg`Skips chapters from aggregator sites, which are usually small already.`,
    rule: { conditions: { tiers: ['aggregator'] }, action: 'skip', stop: null },
  },
  {
    id: 'volumes',
    title: msg`Squeeze volumes harder`,
    hint: msg`Converts imported volumes at Balanced, where the biggest savings are.`,
    rule: { conditions: { tiers: ['volume'] }, action: 'convert', stop: 'balanced' },
  },
  {
    id: 'scanlator',
    title: msg`Keep scanlator releases exact`,
    hint: msg`Converts scanlator chapters at Lossless, so no pixel changes.`,
    rule: { conditions: { tiers: ['scanlator'] }, action: 'convert', stop: 'lossless' },
  },
  {
    id: 'tagged',
    title: msg`Never touch tagged series`,
    hint: msg`Skips every series you tag keep-original.`,
    rule: { conditions: { tags: ['keep-original'] }, action: 'skip', stop: null },
  },
]
