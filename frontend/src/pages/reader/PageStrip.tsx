import { useEffect, useRef } from 'react'

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
  const current = useRef<HTMLButtonElement>(null)

  useEffect(() => {
    current.current?.scrollIntoView({ block: 'nearest', inline: 'center' })
  }, [page])

  return (
    <div className="reader-strip" style={{ flexDirection: rtl ? 'row-reverse' : 'row' }}>
      {urls.map((src, index) => (
        <button
          key={src}
          type="button"
          ref={index === page ? current : undefined}
          className="reader-strip-item"
          data-current={index === page}
          data-bookmarked={bookmarks.has(index)}
          onClick={() => onSelect(index)}
          aria-label={`Go to page ${index + 1}${bookmarks.has(index) ? ' (bookmarked)' : ''}`}
          aria-current={index === page}
        >
          <img src={src} alt="" loading="lazy" decoding="async" draggable={false} />
          <span>{index + 1}</span>
        </button>
      ))}
    </div>
  )
}
