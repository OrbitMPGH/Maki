import { createElement, type CSSProperties } from 'react'

/** Favicons shipped in `public/source-icons/`, keyed by `ISource.Name`. */
export const SOURCE_ICONS: Record<string, string> = {
  asura: '/source-icons/asura.webp',
  atsumaru: '/source-icons/atsumaru.ico',
  flamecomics: '/source-icons/flamecomics.png',
  mangadex: '/source-icons/mangadex.ico',
  mangafire: '/source-icons/mangafire.svg',
  mangakakalot: '/source-icons/mangakakalot.ico',
  mangakatana: '/source-icons/mangakatana.png',
  mangapill: '/source-icons/mangapill.png',
  mangaplus: '/source-icons/mangaplus.ico',
  tcbscans: '/source-icons/tcbscans.png',
  topmanhua: '/source-icons/topmanhua.png',
  webtoons: '/source-icons/webtoons.ico',
  weebcentral: '/source-icons/weebcentral.ico',
  shonenjumpplus: '/source-icons/shonenjumpplus.png',
  comicdays: '/source-icons/comicdays.png',
  sundaywebry: '/source-icons/sundaywebry.png',
  magcomi: '/source-icons/magcomi.png',
  tonarinoyj: '/source-icons/tonarinoyj.png',
  comiczenon: '/source-icons/comiczenon.png',
  kuragebunch: '/source-icons/kuragebunch.png',
  toonily: '/source-icons/toonily.png',
  mangalib: '/source-icons/mangalib.svg',
  dynasty: '/source-icons/dynasty.png',
  animesama: '/source-icons/animesama.png',
  manhwaweb: '/source-icons/manhwaweb.png',
  olympus: '/source-icons/olympus.webp',
  shinigami: '/source-icons/shinigami.png',
  manhwa18net: '/source-icons/manhwa18net.ico',
  cuutruyen: '/source-icons/cuutruyen.ico',
  mangaworld: '/source-icons/mangaworld.png',
  naverwebtoon: '/source-icons/naverwebtoon.ico',
  manhuagui: '/source-icons/manhuagui.ico',
  mangatube: '/source-icons/mangatube.ico',
  mangadenizi: '/source-icons/mangadenizi.ico',
  taiyo: '/source-icons/taiyo.png',
  comicwalker: '/source-icons/comicwalker.png',
  rawkuma: '/source-icons/rawkuma.png',
  teamx: '/source-icons/teamx.png',
}

/** `pt-br` and `pt` count as one language wherever sources are grouped or counted. */
export function baseLanguage(code: string): string {
  return code.toLowerCase().split('-')[0]
}

export function sourceHost(baseUrl: string): string {
  try {
    return new URL(baseUrl).hostname.replace(/^www\./, '')
  } catch {
    return baseUrl
  }
}

function monogram(label: string): string {
  const words = label.replace(/[^\p{L}\p{N} ]/gu, ' ').split(' ').filter(Boolean)
  if (words.length > 1) return (words[0][0] + words[1][0]).toUpperCase()
  return (words[0] ?? label).slice(0, 2).toUpperCase()
}

/**
 * A source's favicon on a neutral tile, or its initials when Maki ships no icon for it. Always
 * square and `size` pixels wide, so rows and stacks line up whichever one renders.
 */
export function SourceIcon({
  name,
  label,
  size = 28,
  className,
  style,
}: {
  name: string
  /** Display name, used for the monogram fallback. */
  label?: string
  size?: number
  className?: string
  style?: CSSProperties
}) {
  const src = SOURCE_ICONS[name]
  return createElement(
    'span',
    {
      className: className ? `source-icon ${className}` : 'source-icon',
      style: { width: size, height: size, fontSize: Math.round(size * 0.38), ...style },
      'aria-hidden': true,
    },
    src ? createElement('img', { src, alt: '', loading: 'lazy' }) : monogram(label ?? name),
  )
}
