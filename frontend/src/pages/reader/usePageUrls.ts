import { useEffect, useRef, useState } from 'react'
import { api } from '../../api/client'
import { pageUrl, type ReaderManifest } from '../../api/reader'

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

/**
 * Fetches the first `count` pages of another chapter so a turn into it lands on loaded images.
 * The page URLs carry the chapter's `pageVersion`, which only its manifest knows, and a URL without
 * it would warm a cache entry the real load never asks for. The manifest is read straight from the
 * API rather than through React Query: `goToChapter` drops the target's cached manifest on purpose
 * (its `resumePage` goes stale), and a warm-up must not leave one behind. Neither request writes
 * reading progress.
 */
export function useWarmChapter(chapterId: number | null, active: boolean, count: number) {
  const known = useRef<{ chapterId: number; pageCount: number; version: string } | null>(null)

  useEffect(() => {
    if (chapterId == null || !active || count <= 0) return
    let cancelled = false
    const held: HTMLImageElement[] = []

    void (async () => {
      let info = known.current?.chapterId === chapterId ? known.current : null
      if (!info) {
        const manifest = await api<ReaderManifest>(`/reader/chapter/${chapterId}`)
        info = { chapterId, pageCount: manifest.pageCount, version: manifest.pageVersion }
        known.current = info
      }
      for (let i = 0; i < Math.min(count, info.pageCount); i++) {
        const src = await pageUrl(chapterId, i, false, info.version)
        if (cancelled) return
        const image = new Image()
        image.src = src
        held.push(image)
      }
    })().catch(() => {})

    return () => {
      cancelled = true
      for (const image of held) image.src = ''
    }
  }, [chapterId, active, count])
}
