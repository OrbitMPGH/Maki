import { http, HttpResponse, type HttpHandler } from 'msw'
import type {
  PageConversionRule,
  PageConversionRules,
  PageConversionSample,
  PageConversionSamples,
  PageConversionSettings,
  PageConversionStatus,
  PageConversionScope,
  PageConversionScopeRequest,
  PageConversionStop,
} from '../../../src/api/pageConversion'
import { api } from '../api'
import { seriesById } from '../shared'
import { pageConversionIdle } from './data'

export const sampleGreyscale = {
  fileId: 1090,
  chapterId: 1090,
  seriesId: 1,
  seriesTitle: 'Lanterns Over Kasumi',
  chapterLabel: 'Vol.1 Ch.9',
  page: 14,
  pageCount: 38,
  reason: 'greyscale',
  tier: 'volume',
  fileSize: 52_428_800,
} satisfies PageConversionSample

export const sampleColour = {
  fileId: 6120,
  chapterId: 6120,
  seriesId: 6,
  seriesTitle: 'Ashen Crown Regression',
  chapterLabel: 'Ch.12',
  page: 21,
  pageCount: 62,
  reason: 'colour',
  tier: 'aggregator',
  fileSize: 9_437_184,
} satisfies PageConversionSample

export const sampleHeaviest = {
  fileId: 3050,
  chapterId: 3050,
  seriesId: 3,
  seriesTitle: 'Iron Petal Academy',
  chapterLabel: 'Ch.5',
  page: 9,
  pageCount: 24,
  reason: 'heaviest',
  tier: 'scanlator',
  fileSize: 31_457_280,
} satisfies PageConversionSample

export const pageConversionSamples = {
  samples: [sampleGreyscale, sampleColour, sampleHeaviest],
} satisfies PageConversionSamples

export const ruleLeaveScraped = {
  id: 1,
  name: 'Leave scraped chapters alone',
  conditions: { tiers: ['aggregator'] },
  action: 'skip',
  stop: null,
} satisfies PageConversionRule

export const ruleSqueezeVolumes = {
  id: 2,
  name: 'Squeeze volumes harder',
  conditions: { tiers: ['volume'], sizeOverBytes: 100 * 1024 * 1024 },
  action: 'convert',
  stop: 'balanced',
} satisfies PageConversionRule

export const pageConversionRules = { rules: [ruleLeaveScraped, ruleSqueezeVolumes] } satisfies PageConversionRules

const GB = 1024 ** 3
const MB = 1024 ** 2

/** Counts for the first rules; a rule added after those gets a small one. */
const ruleBuckets = [
  { files: 1240, bytes: Math.round(3.6 * GB) },
  { files: 365, bytes: Math.round(73.8 * GB) },
]

export const pageConversionScope = {
  rules: ruleBuckets,
  default: { files: 94, bytes: Math.round(1.9 * GB) },
  overrides: { files: 18, bytes: Math.round(2.1 * GB) },
  alreadyCompact: { files: 41, bytes: Math.round(0.9 * GB) },
  convertedEarlier: { files: 412, bytes: Math.round(31.4 * GB) },
} satisfies PageConversionScope

const scopeFor = (request: PageConversionScopeRequest): PageConversionScope => ({
  ...pageConversionScope,
  rules: request.rules.map((_, i) => ruleBuckets[i] ?? { files: 12, bytes: 400 * MB }),
})

export const ORIGINAL_BYTES = 1_240_000

/** Median page's share of the original per stop, from the design doc's calibration. */
const SHARE: Record<'webp' | 'avif', Record<PageConversionStop, number>> = {
  webp: { smallest: 0.37, compact: 0.45, balanced: 0.54, high: 0.8, lossless: 1 },
  avif: { smallest: 0.27, compact: 0.38, balanced: 0.43, high: 0.62, lossless: 1 },
}

const BLUR: Record<PageConversionStop, number> = { smallest: 1.4, compact: 0.9, balanced: 0.5, high: 0.15, lossless: 0 }

/** A page with screentone and thin lines, which is where the stops differ; softer the lower the stop. */
function pageSvg(blur: number, hue: number): string {
  const filter = blur > 0 ? `<filter id="soft"><feGaussianBlur stdDeviation="${blur}"/></filter>` : ''
  const apply = blur > 0 ? ' filter="url(#soft)"' : ''
  const ink = `hsl(${hue} 20% 14%)`
  return `<svg xmlns="http://www.w3.org/2000/svg" width="900" height="1300" viewBox="0 0 900 1300">
<defs>
<pattern id="tone" width="6" height="6" patternUnits="userSpaceOnUse"><circle cx="3" cy="3" r="1.3" fill="${ink}"/></pattern>
<pattern id="fine" width="3" height="3" patternUnits="userSpaceOnUse"><circle cx="1.5" cy="1.5" r="0.6" fill="${ink}"/></pattern>
${filter}
</defs>
<rect width="900" height="1300" fill="#fbfaf6"/>
<g${apply}>
<rect x="60" y="60" width="780" height="520" fill="none" stroke="${ink}" stroke-width="3"/>
<rect x="60" y="60" width="780" height="520" fill="url(#tone)" opacity="0.55"/>
<circle cx="300" cy="320" r="150" fill="#fbfaf6" stroke="${ink}" stroke-width="2"/>
<circle cx="300" cy="320" r="150" fill="url(#fine)"/>
<path d="M520 120 L760 520 M540 120 L780 520 M560 120 L800 520 M580 120 L820 520" stroke="${ink}" stroke-width="1"/>
<rect x="60" y="620" width="380" height="620" fill="url(#tone)" opacity="0.8" stroke="${ink}" stroke-width="3"/>
<rect x="470" y="620" width="370" height="290" fill="none" stroke="${ink}" stroke-width="3"/>
<rect x="470" y="950" width="370" height="290" fill="url(#fine)" stroke="${ink}" stroke-width="3"/>
<g stroke="${ink}" stroke-width="1">${Array.from({ length: 22 }, (_, i) => `<line x1="490" y1="${650 + i * 12}" x2="${820 - (i % 5) * 30}" y2="${650 + i * 12}"/>`).join('')}</g>
</g>
</svg>`
}

const fileHue = (fileId: number) => (fileId * 47) % 360

const sizeHeaders = (encoded: number, keptReason?: string) => ({
  'Content-Type': 'image/svg+xml',
  'X-Maki-Original-Bytes': String(ORIGINAL_BYTES),
  'X-Maki-Encoded-Bytes': String(encoded),
  ...(keptReason ? { 'X-Maki-Kept': '1', 'X-Maki-Kept-Reason': keptReason } : {}),
})

/** Every encoded preview comes back kept, for stories that show a reason line. */
export const keptPreviewHandler = (reason: string) =>
  http.get(api('/pageconversion/preview/:fileId/:page'), ({ params, request }) => {
    const hue = fileHue(Number(params.fileId))
    const original = new URL(request.url).searchParams.get('original') === 'true'
    return new HttpResponse(pageSvg(0, hue), { headers: sizeHeaders(ORIGINAL_BYTES, original ? undefined : reason) })
  })

export const pageConversionHandlers: HttpHandler[] = [
  http.put(api('/pageconversion/plan'), async ({ request }) => {
    const { settings } = (await request.json()) as { settings: PageConversionSettings }
    return HttpResponse.json({ ...pageConversionIdle, settings } satisfies PageConversionStatus)
  }),
  http.get(api('/pageconversion/rules'), () => HttpResponse.json(pageConversionRules)),
  http.put(api('/pageconversion/rules'), async ({ request }) => {
    const { rules } = (await request.json()) as PageConversionRules
    return HttpResponse.json({ rules: rules.map((rule, i) => ({ ...rule, id: rule.id ?? 100 + i })) } satisfies PageConversionRules)
  }),
  http.post(api('/pageconversion/scope'), async ({ request }) =>
    HttpResponse.json(scopeFor((await request.json()) as PageConversionScopeRequest)),
  ),
  http.get(api('/pageconversion/samples'), () => HttpResponse.json(pageConversionSamples)),
  http.get(api('/pageconversion/samples/:fileId'), ({ params }) => {
    const fileId = Number(params.fileId)
    const series = seriesById(Math.floor(fileId / 1000))
    return HttpResponse.json({
      fileId,
      chapterId: fileId,
      seriesId: series.id,
      seriesTitle: series.title,
      chapterLabel: `Ch.${(fileId % 1000) / 10}`,
      page: 11,
      pageCount: 32,
      reason: 'custom',
      tier: 'scanlator',
      fileSize: 20_971_520,
    } satisfies PageConversionSample)
  }),
  http.get(api('/pageconversion/preview/:fileId/:page'), ({ params, request }) => {
    const fileId = Number(params.fileId)
    const query = new URL(request.url).searchParams
    const hue = fileHue(fileId)
    if (query.get('original') === 'true') {
      return new HttpResponse(pageSvg(0, hue), { headers: sizeHeaders(ORIGINAL_BYTES) })
    }
    const stop = (query.get('stop') ?? 'high') as PageConversionStop
    const format = query.get('format') === 'avif' ? 'avif' : 'webp'
    if (stop === 'lossless') return new HttpResponse(pageSvg(0, hue), { headers: sizeHeaders(ORIGINAL_BYTES, 'lossless') })
    const encoded = Math.round(ORIGINAL_BYTES * SHARE[format][stop])
    return new HttpResponse(pageSvg(BLUR[stop], hue), { headers: sizeHeaders(encoded) })
  }),
]
