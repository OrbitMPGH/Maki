import { http, HttpResponse, type HttpHandler } from 'msw'
import type {
  EstimateBucket,
  PageConversionEstimate,
  PageConversionPlanRequest,
  PageConversionRule,
  PageConversionRules,
  PageConversionSample,
  PageConversionSamples,
  PageConversionSettings,
  PageConversionStatus,
  PageConversionScope,
  PageConversionScopeRequest,
  PageConversionStop,
  PageSplitPreview,
  RowSamplesRequest,
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

/** A second colour page, so the colour sheet has more than one to pick from. */
export const sampleColourSecond = {
  fileId: 6240,
  chapterId: 6240,
  seriesId: 6,
  seriesTitle: 'Ashen Crown Regression',
  chapterLabel: 'Ch.24',
  page: 30,
  pageCount: 58,
  reason: 'colour',
  tier: 'aggregator',
  fileSize: 10_485_760,
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

const noBucket = {
  skip: false,
  files: 0,
  bytes: 0,
  sampledFiles: 0,
  expectedLow: 0,
  expectedHigh: 0,
  hardlinkedBytes: 0,
  secondsLow: 0,
  secondsHigh: 0,
} satisfies EstimateBucket

export const estimateSkipBucket = {
  ...noBucket,
  skip: true,
  files: 1527,
  bytes: Math.round(18.1 * GB),
} satisfies EstimateBucket

export const estimateConvertBucket = {
  ...noBucket,
  files: 365,
  bytes: Math.round(73.8 * GB),
  sampledFiles: 12,
  expectedLow: Math.round(48 * GB),
  expectedHigh: Math.round(53 * GB),
  hardlinkedBytes: Math.round(32.9 * GB),
  secondsLow: 35 * 60,
  secondsHigh: 70 * 60,
} satisfies EstimateBucket

export const estimateDefaultBucket = {
  ...noBucket,
  files: 94,
  bytes: Math.round(1.9 * GB),
  sampledFiles: 12,
  expectedLow: Math.round(1.2 * GB),
  expectedHigh: Math.round(1.4 * GB),
  secondsLow: 4 * 60,
  secondsHigh: 9 * 60,
} satisfies EstimateBucket

export const estimateResult = {
  rules: [estimateSkipBucket, estimateConvertBucket],
  default: estimateDefaultBucket,
  overrides: { ...noBucket, files: 18, bytes: Math.round(2.1 * GB), sampledFiles: 6, expectedLow: Math.round(1.1 * GB), expectedHigh: Math.round(1.3 * GB), secondsLow: 90, secondsHigh: 240 },
  passedOver: {
    hardlinkedFiles: 143,
    hardlinkedBytes: Math.round(32.9 * GB),
    cleanupDue: 22,
    alreadyCompact: 41,
    downloadInFlight: 3,
    pendingImport: 9,
    skippedByRule: 1527,
    skippedByRuleBytes: Math.round(18.1 * GB),
    neverSeries: 2,
    neverFiles: 56,
    neverBytes: Math.round(4.2 * GB),
    undone: 7,
    alreadyChecked: 212,
    alreadyCheckedBytes: Math.round(5.6 * GB),
  },
  convertedEarlier: [
    { checkedWith: 'webp:high', files: 1204, savedBytes: Math.round(9.8 * GB) },
    { checkedWith: 'avif:balanced+png', files: 80, savedBytes: Math.round(2.4 * GB) },
    { checkedWith: 'unknown', files: 12, savedBytes: Math.round(0.3 * GB) },
  ],
  convertedWithOriginalInTrash: 37,
  msPerMegapixel: 410,
} satisfies NonNullable<PageConversionEstimate['result']>

const stamp = '2026-10-19T11:50:00Z'

export const estimateIdle = {
  state: 'idle',
  done: 0,
  total: 0,
  result: null,
  error: null,
  startedAtUtc: null,
  finishedAtUtc: null,
  planHash: null,
} satisfies PageConversionEstimate

export const estimateDone = {
  state: 'done',
  done: 36,
  total: 36,
  result: estimateResult,
  error: null,
  startedAtUtc: stamp,
  finishedAtUtc: '2026-10-19T11:52:10Z',
  planHash: 'plan-a',
} satisfies PageConversionEstimate

export const estimateRunning = {
  ...estimateIdle,
  state: 'running',
  done: 14,
  total: 36,
  startedAtUtc: stamp,
  planHash: 'plan-a',
} satisfies PageConversionEstimate

export const estimateFailed = {
  ...estimateIdle,
  state: 'failed',
  done: 9,
  total: 36,
  error: 'The library could not be read while sampling.',
  startedAtUtc: stamp,
  finishedAtUtc: '2026-10-19T11:50:40Z',
  planHash: 'plan-a',
} satisfies PageConversionEstimate

/** What the estimate would be for a request: skip rules count their files, convert rules get the sampled figures. */
const estimateFor = (request: PageConversionPlanRequest): PageConversionEstimate => ({
  ...estimateDone,
  result: {
    ...estimateResult,
    rules: request.rules.map((rule) => (rule.action === 'skip' ? estimateSkipBucket : estimateConvertBucket)),
  },
})

/** Starting an estimate answers as the server does: running, with nothing sampled yet. */
export const startedEstimate = { ...estimateRunning, done: 0 } satisfies PageConversionEstimate

export const ORIGINAL_BYTES = 1_240_000

/** Median page's share of the original per stop, from the design doc's calibration. */
const SHARE: Record<'webp' | 'avif' | 'jxl', Record<PageConversionStop, number>> = {
  jxl: { smallest: 0.38, compact: 0.45, balanced: 0.53, high: 0.71, lossless: 1 },
  webp: { smallest: 0.37, compact: 0.45, balanced: 0.54, high: 0.8, lossless: 1 },
  avif: { smallest: 0.27, compact: 0.38, balanced: 0.43, high: 0.62, lossless: 1 },
}

const BLUR: Record<PageConversionStop, number> = { smallest: 1.4, compact: 0.9, balanced: 0.5, high: 0.15, lossless: 0 }

/** A colour page: flat fills and a gradient sky, where colour artefacts show. */
function colourSvg(blur: number, hue: number): string {
  const filter = blur > 0 ? `<filter id="soft"><feGaussianBlur stdDeviation="${blur}"/></filter>` : ''
  const apply = blur > 0 ? ' filter="url(#soft)"' : ''
  return `<svg xmlns="http://www.w3.org/2000/svg" width="900" height="1300" viewBox="0 0 900 1300">
<defs>
<linearGradient id="sky" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="hsl(${hue} 60% 22%)"/><stop offset="1" stop-color="hsl(${(hue + 40) % 360} 70% 68%)"/></linearGradient>
${filter}
</defs>
<rect width="900" height="1300" fill="#fbfaf6"/>
<g${apply}>
<rect x="60" y="60" width="780" height="560" fill="url(#sky)" stroke="#1d1b22" stroke-width="3"/>
<circle cx="640" cy="230" r="90" fill="hsl(${(hue + 180) % 360} 80% 70%)"/>
<path d="M60 620 L320 380 L520 560 L700 420 L840 620 Z" fill="hsl(${(hue + 120) % 360} 35% 30%)"/>
<rect x="60" y="660" width="380" height="580" fill="hsl(${(hue + 20) % 360} 55% 58%)" stroke="#1d1b22" stroke-width="3"/>
<circle cx="250" cy="900" r="110" fill="#f2d2b6" stroke="#1d1b22" stroke-width="2"/>
<rect x="470" y="660" width="370" height="580" fill="hsl(${(hue + 210) % 360} 40% 82%)" stroke="#1d1b22" stroke-width="3"/>
<g stroke="#1d1b22" stroke-width="1">${Array.from({ length: 18 }, (_, i) => `<line x1="490" y1="${700 + i * 14}" x2="${820 - (i % 4) * 40}" y2="${700 + i * 14}"/>`).join('')}</g>
</g>
</svg>`
}

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

/** The suggested colour page and the tall strip are in colour; every other mock page is black and white. */
const isColourFile = (fileId: number) =>
  fileId === sampleColour.fileId || fileId === sampleColourSecond.fileId || fileId === TALL_FILE_ID

const pageFor = (fileId: number, blur: number) =>
  isColourFile(fileId) ? colourSvg(blur, fileHue(fileId)) : pageSvg(blur, fileHue(fileId))

const sizeHeaders = (encoded: number, keptReason?: string, colour = false) => ({
  'Content-Type': 'image/svg+xml',
  'X-Maki-Original-Bytes': String(ORIGINAL_BYTES),
  'X-Maki-Encoded-Bytes': String(encoded),
  'X-Maki-Colour': colour ? '1' : '0',
  ...(keptReason ? { 'X-Maki-Kept': '1', 'X-Maki-Kept-Reason': keptReason } : {}),
})

/** Every encoded preview comes back kept, for stories that show a reason line. */
export const keptPreviewHandler = (reason: string) =>
  http.get(api('/pageconversion/preview/:fileId/:page'), ({ params, request }) => {
    const hue = fileHue(Number(params.fileId))
    const original = new URL(request.url).searchParams.get('original') === 'true'
    return new HttpResponse(pageSvg(0, hue), { headers: sizeHeaders(ORIGINAL_BYTES, original ? undefined : reason) })
  })

export const TALL_FILE_ID = 7300
const TALL_WIDTH = 720
const TALL_HEIGHT = 15200
/** Gutters between panels in the mock strip, as [top, bottom] rows. */
const TALL_GUTTERS = [1700, 3900, 6300, 8800, 11200, 13600].map((top) => [top, top + 140])

/** A webtoon strip: coloured panels with white gutters at TALL_GUTTERS. */
function stripSvg(): string {
  const panels: string[] = []
  let top = 0
  TALL_GUTTERS.forEach(([start, end], i) => {
    panels.push(`<rect x="0" y="${top}" width="${TALL_WIDTH}" height="${start - top}" fill="hsl(${(i * 53) % 360} 45% 62%)"/>`)
    top = end
  })
  panels.push(`<rect x="0" y="${top}" width="${TALL_WIDTH}" height="${TALL_HEIGHT - top}" fill="hsl(300 45% 62%)"/>`)
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${TALL_WIDTH}" height="${TALL_HEIGHT}" viewBox="0 0 ${TALL_WIDTH} ${TALL_HEIGHT}">
<rect width="${TALL_WIDTH}" height="${TALL_HEIGHT}" fill="#ffffff"/>${panels.join('')}</svg>`
}

/** The cuts the server would plan: the last gutter that starts within 40% above each height step. */
export function splitFor(height: number): PageSplitPreview {
  const cuts: number[] = []
  let start = 0
  while (TALL_HEIGHT - start > height) {
    const target = start + height
    const gutter = [...TALL_GUTTERS].reverse().find(([top]) => top <= target && top >= target - height * 0.4)
    const cut = gutter ? Math.min(Math.floor((gutter[0] + gutter[1]) / 2), target) : target
    if (TALL_HEIGHT - cut < height / 5) break
    cuts.push(cut)
    start = cut
  }
  return {
    sample: {
      fileId: TALL_FILE_ID,
      chapterId: TALL_FILE_ID,
      seriesId: 7,
      seriesTitle: 'Tower of the Long Night',
      chapterLabel: 'Ch.41',
      page: 3,
      pageCount: 9,
      reason: 'tall',
      tier: 'aggregator',
      fileSize: 31_457_280,
    },
    width: TALL_WIDTH,
    height: TALL_HEIGHT,
    cuts,
  }
}

export const splitHandlers: HttpHandler[] = [
  http.get(api('/pageconversion/split'), ({ request }) =>
    HttpResponse.json(splitFor(Number(new URL(request.url).searchParams.get('height') ?? 5000))),
  ),
  http.get(api(`/pageconversion/preview/${TALL_FILE_ID}/:page`), () =>
    new HttpResponse(stripSvg(), { headers: sizeHeaders(ORIGINAL_BYTES) }),
  ),
]

export const pageConversionHandlers: HttpHandler[] = [
  ...splitHandlers,
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
  http.get(api('/pageconversion/estimate'), () => HttpResponse.json(estimateIdle)),
  http.post(api('/pageconversion/estimate'), async ({ request }) =>
    HttpResponse.json(estimateFor((await request.json()) as PageConversionPlanRequest)),
  ),
  http.get(api('/pageconversion/samples'), () => HttpResponse.json(pageConversionSamples)),
  http.post(api('/pageconversion/samples/row'), async ({ request }) => {
    const { kind } = (await request.json()) as RowSamplesRequest
    return HttpResponse.json({
      samples: kind === 'colour' ? [sampleColour, sampleColourSecond] : [sampleGreyscale, sampleHeaviest],
    } satisfies PageConversionSamples)
  }),
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
    const colour = isColourFile(fileId)
    if (query.get('original') === 'true') {
      return new HttpResponse(pageFor(fileId, 0), { headers: sizeHeaders(ORIGINAL_BYTES, undefined, colour) })
    }
    const stop = (query.get('stop') ?? 'high') as PageConversionStop
    const asked = query.get('format')
    const format = asked === 'avif' || asked === 'jxl' ? asked : 'webp'
    if (stop === 'lossless') {
      return new HttpResponse(pageFor(fileId, 0), { headers: sizeHeaders(ORIGINAL_BYTES, 'lossless', colour) })
    }
    const encoded = Math.round(ORIGINAL_BYTES * SHARE[format][stop])
    return new HttpResponse(pageFor(fileId, BLUR[stop]), { headers: sizeHeaders(encoded, undefined, colour) })
  }),
]
