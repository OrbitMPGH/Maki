import { useEffect, useRef } from 'react'
import { useLingui } from '@lingui/react/macro'

/**
 * Thumbnail rail for jumping around a chapter.
 *
 * Plain DOM and CSS classes rather than Mantine components, and no backdrop-filter: the same
 * discipline CoverCard documents: a few hundred thumbnails reconciling on every page turn is
 * exactly where the heavier components start to cost.
 */
export default function PageStrip({
  urls,
  page,
  bookmarks,
  onSelect,
  rtl,
}: {
  urls: string[]
  page: number
  /** Bookmarked page indices, marked here so a bookmark is findable, not just settable. */
  bookmarks: Set<number>
  onSelect: (page: number) => void
  rtl: boolean
}) {
  const { t } = useLingui()
  const current = useRef<HTMLButtonElement>(null)

  useEffect(() => {
    current.current?.scrollIntoView({ block: 'nearest', inline: 'center' })
  }, [page])

  return (
    <div className="reader-strip" style={{ flexDirection: rtl ? 'row-reverse' : 'row' }}>
      {urls.map((src, index) => {
        const pageNumber = index + 1
        const bookmarked = bookmarks.has(index)
        return (
          <button
            key={src}
            type="button"
            ref={index === page ? current : undefined}
            className="reader-strip-item"
            data-current={index === page}
            data-bookmarked={bookmarked}
            onClick={() => onSelect(index)}
            aria-label={bookmarked ? t`Go to page ${pageNumber} (bookmarked)` : t`Go to page ${pageNumber}`}
            aria-current={index === page}
          >
            <img src={src} alt="" loading="lazy" decoding="async" draggable={false} />
            <span>{pageNumber}</span>
          </button>
        )
      })}
    </div>
  )
}
