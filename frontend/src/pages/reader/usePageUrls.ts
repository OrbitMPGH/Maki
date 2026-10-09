import { useEffect, useRef, useState } from 'react'
import { pageUrl } from '../../api/reader'

/**
 * Resolves every page's URL once per chapter. The URLs need the API key, which arrives from an
 * async bootstrap fetch, so they can't be computed inline during render.
 */
export function usePageUrls(chapterId: number, pageCount: number, version: string | undefined, thumb = false) {
  const [urls, setUrls] = useState<string[]>([])

  useEffect(() => {
    let cancelled = false
    if (!pageCount) {
      setUrls([])
      return
    }

    void Promise.all(
      Array.from({ length: pageCount }, (_, i) => pageUrl(chapterId, i, thumb, version)),
    ).then((resolved) => {
      if (!cancelled) setUrls(resolved)
    })

    return () => {
      cancelled = true
    }
  }, [chapterId, pageCount, version, thumb])

  return urls
}

/** The page itself and the `count` after it, for a reader that turns one page at a time. */
export function pagesFrom(page: number, count: number): number[] {
  return count <= 0 ? [] : Array.from({ length: count + 1 }, (_, i) => page + i)
}

/**
 * Warms the browser cache and decodes the given pages ahead of the turn. Images stay loaded while
 * their page remains in the window, so a turn only starts the fetches that are actually new.
 * `onMeasure` receives each page's size as it loads, which is how double-page pairing learns
 * about wide pages before they are reached.
 */
export function usePreload(
  urls: string[],
  pages: number[],
  onMeasure?: (index: number, image: HTMLImageElement) => void,
) {
  const loaded = useRef(new Map<string, HTMLImageElement>())
  const measure = useRef(onMeasure)
  measure.current = onMeasure

  // Dropping the src lets the browser abandon in-flight fetches when the reader closes or the
  // chapter changes.
  useEffect(() => {
    const images = loaded.current
    return () => {
      for (const image of images.values()) {
        image.onload = null
        image.src = ''
      }
      images.clear()
    }
  }, [urls])

  useEffect(() => {
    const images = loaded.current
    const wanted = new Set<string>()
    for (const index of pages) {
      const src = urls[index]
      if (!src) continue
      wanted.add(src)
      if (images.has(src)) continue
      const image = new Image()
      image.onload = () => measure.current?.(index, image)
      image.src = src
      void image.decode().catch(() => {})
      images.set(src, image)
    }
    for (const [src, image] of images) {
      if (wanted.has(src)) continue
      image.onload = null
      image.src = ''
      images.delete(src)
    }
  }, [urls, pages])
}
