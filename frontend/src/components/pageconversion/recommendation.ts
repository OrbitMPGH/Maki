import { useEffect, useState } from 'react'
import { msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { colourTargetOf, sameTarget, type PageConversionSettings, type PageTarget } from '../../api/pageConversion'
import { jpegXlSupported, jpegXlSupportedNow } from '../../lib/enhance/jpegXl'

/** Where the library is read, widest last: each place also covers the ones before it. */
export type ReadingPlace = 'maki' | 'apps' | 'ereaders'

export const READING_PLACES: readonly ReadingPlace[] = ['maki', 'apps', 'ereaders']

export const READING_PLACE_LABELS: Record<ReadingPlace, MessageDescriptor> = {
  maki: msg`Only in Maki`,
  apps: msg`Also in apps like Mihon`,
  ereaders: msg`Also in KOReader or on an e-reader`,
}

/** Why JPEG XL is left out of the Maki recommendation, or null when it is in. */
export type JpegXlGap = 'server' | 'browser' | null

export interface Recommendation {
  target: PageTarget
  line: MessageDescriptor
}

export interface Recommendations {
  grey: Recommendation
  colour: Recommendation
}

/**
 * AVIF Compact and JPEG XL High where only Maki reads the pages. Apps reading over Komga or OPDS get
 * JPEG XL pages as WebP made on the fly, so AVIF High takes colour pages there; KOReader opens
 * neither, so WebP takes both. Every format's stops are calibrated to the same scores.
 */
export function recommendationsFor(place: ReadingPlace, gap: JpegXlGap): Recommendations {
  if (place === 'ereaders') {
    const line = msg`Recommended when KOReader or an e-reader reads your library.`
    return {
      grey: { target: { format: 'webp', stop: 'compact' }, line },
      colour: { target: { format: 'webp', stop: 'high' }, line },
    }
  }
  const grey: Recommendation = {
    target: { format: 'avif', stop: 'compact' },
    line: msg`Recommended for most libraries, with no difference you would notice while reading.`,
  }
  if (place === 'apps') {
    return {
      grey,
      colour: { target: { format: 'avif', stop: 'high' }, line: msg`Recommended when apps like Mihon read your library.` },
    }
  }
  if (gap === 'server') {
    return {
      grey,
      colour: { target: { format: 'avif', stop: 'high' }, line: msg`Recommended, since this server cannot write JPEG XL.` },
    }
  }
  if (gap === 'browser') {
    return {
      grey,
      colour: { target: { format: 'avif', stop: 'high' }, line: msg`Recommended, since this browser cannot show JPEG XL.` },
    }
  }
  return { grey, colour: { target: { format: 'jxl', stop: 'high' }, line: msg`Recommended if you read in Maki.` } }
}

/** The settings a place's recommendation sets on the plan's default. */
export function recommendedSettings(place: ReadingPlace, gap: JpegXlGap): Partial<PageConversionSettings> {
  const { grey, colour } = recommendationsFor(place, gap)
  return { format: grey.target.format, stop: grey.target.stop, colourFormat: colour.target.format, colourStop: colour.target.stop }
}

/** The place whose recommendation the plan's default matches, or null when it was set by hand. */
export function placeOf(settings: PageConversionSettings, gap: JpegXlGap): ReadingPlace | null {
  if (!settings.format) return null
  const grey: PageTarget = { format: settings.format, stop: settings.stop }
  const colour = colourTargetOf(settings) ?? grey
  return (
    READING_PLACES.find((place) => {
      const r = recommendationsFor(place, gap)
      return sameTarget(grey, r.grey.target) && sameTarget(colour, r.colour.target)
    }) ?? null
  )
}

/** Whether this browser and the server can both handle JPEG XL. Assumes the browser can until its probe settles. */
export function useJpegXlGap(serverAvailable: boolean | undefined): JpegXlGap {
  const [browser, setBrowser] = useState<boolean | null>(jpegXlSupportedNow)
  useEffect(() => {
    if (browser !== null) return
    let live = true
    void jpegXlSupported().then((supported) => live && setBrowser(supported))
    return () => {
      live = false
    }
  }, [browser])
  if (serverAvailable === false) return 'server'
  return browser === false ? 'browser' : null
}
