import { useEffect, useState } from 'react'
import { pageUrl } from '../../api/reader'

/**
 * Resolves every page's URL once per chapter. The URLs need the API key, which arrives from an
 * async bootstrap fetch, so they can't be computed inline during render.
 */
export function usePageUrls(chapterId: number, pageCount: number, thumb = false) {
  const [urls, setUrls] = useState<string[]>([])

  useEffect(() => {
    let cancelled = false
    if (!pageCount) {
      setUrls([])
      return
    }

    void Promise.all(
      Array.from({ length: pageCount }, (_, i) => pageUrl(chapterId, i, thumb)),
    ).then((resolved) => {
      if (!cancelled) setUrls(resolved)
    })

    return () => {
      cancelled = true
    }
  }, [chapterId, pageCount, thumb])

  return urls
}

/** Warms the browser cache with the next few pages so a page turn is instant. */
export function usePreload(urls: string[], page: number, count: number) {
  useEffect(() => {
    if (count <= 0) return
    const images: HTMLImageElement[] = []
    for (let i = page + 1; i <= page + count && i < urls.length; i++) {
      const image = new Image()
      image.src = urls[i]
      images.push(image)
    }
    return () => {
      // Dropping the src lets the browser abandon in-flight fetches when the reader closes.
      for (const image of images) image.src = ''
    }
  }, [urls, page, count])
}
