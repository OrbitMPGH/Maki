import { Button, Center, Loader, Stack, Text } from '@mantine/core'
import { useQueryClient } from '@tanstack/react-query'
import { useCallback, useEffect, useMemo, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import {
  flushProgress,
  useBookmarks,
  useReaderManifest,
  useToggleBookmark,
} from '../../api/reader'
import ContinuousView from './ContinuousView'
import PagedView from './PagedView'
import PageStrip from './PageStrip'
import ReaderToolbar from './ReaderToolbar'
import { useReaderPrefs } from './prefs'
import { usePageUrls, usePreload } from './usePageUrls'
import { useReaderProgress } from './useReaderProgress'
import { useReadingClock } from './useReadingClock'
import { spreadIndexOf, usePageAspects, useSpreads } from './useSpreads'

const ZOOM_STEP = 0.25
const ZOOM_MAX = 4
const CHROME_HIDE_MS = 4000

/**
 * The chromeless reader. Rendered outside the AppShell (see App.tsx) so it owns the whole
 * viewport, and always dark regardless of the theme preset, the same choice the Rewind overlay
 * makes, because page art has to sit on neutral black.
 */
export default function ReaderPage() {
  const { chapterId: param } = useParams()
  const chapterId = Number(param)
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const { data: manifest, isLoading, isError, isFetching } = useReaderManifest(chapterId)
  const { prefs, update, selection, setSelection, source, autoProfileId, profiles } =
    useReaderPrefs(manifest)

  const [page, setPage] = useState(0)
  // Bumped on every *explicit* jump (resume, toolbar scrub, page-strip click, Home/End) so
  // ContinuousView knows to scroll. Plain page updates from its own scroll tracking don't touch
  // this: scrolling to match a page the user just scrolled to would fight the scroll itself.
  const [seekVersion, setSeekVersion] = useState(0)
  const seekToPage = useCallback((index: number) => {
    setPage(index)
    setSeekVersion((v) => v + 1)
  }, [])
  /** The chapter whose saved position has been applied; gates every progress write. */
  const [resumedFor, setResumedFor] = useState<number | null>(null)
  // The chrome starts hidden and is summoned by a tap in the middle of the page: the art gets
  // the whole viewport until you ask for controls.
  const [chrome, setChrome] = useState(false)
  const [chromeHeld, setChromeHeld] = useState(false)
  const [fullscreen, setFullscreen] = useState(false)
  const [stripOpen, setStripOpen] = useState(false)
  const [zoom, setZoom] = useState(1)
  const [incognito, setIncognito] = useState(false)
  const [atEnd, setAtEnd] = useState(false)

  const pageCount = manifest?.pageCount ?? 0
  const urls = usePageUrls(chapterId, pageCount)
  const thumbs = usePageUrls(chapterId, stripOpen ? pageCount : 0, true)
  const { wide, measure } = usePageAspects(urls)
  const spreads = useSpreads(pageCount, wide, prefs.mode === 'double')
  const spreadIndex = useMemo(() => spreadIndexOf(spreads, page), [spreads, page])

  const { data: bookmarks } = useBookmarks(chapterId)
  const toggleBookmark = useToggleBookmark(chapterId)
  const bookmarkedPages = useMemo(
    () => new Set((bookmarks ?? []).map((b) => b.pageIndex)),
    [bookmarks],
  )
  const bookmarked = bookmarkedPages.has(page)

  usePreload(urls, page, prefs.mode === 'vertical' ? 0 : prefs.preload)
  // The position writer stays off until the chapter has resumed. `page` is 0 until then, and
  // writing that would overwrite the saved position with page 1, the very thing being resumed to.
  const tracking = resumedFor === manifest?.chapterId && !incognito
  // Lives here rather than inside the progress hook so a chapter change can hand its banked
  // seconds to the same flush that writes the position out.
  const clock = useReadingClock(tracking)
  useReaderProgress(manifest?.chapterId, page, tracking, clock)

  /**
   * Resume where the chapter was left off, once per chapter, and only off a freshly fetched
   * manifest. React Query serves the cached one first on a reopen, and its `resumePage` is a
   * snapshot from the previous visit: applying it would jump to page 1 and then save that.
   */
  useEffect(() => {
    if (!manifest || isFetching || resumedFor === manifest.chapterId) return
    setResumedFor(manifest.chapterId)
    seekToPage(manifest.resumePage)
    setZoom(1)
    setAtEnd(false)
  }, [manifest, isFetching, resumedFor, seekToPage])

  // Own the viewport: no page scrolling behind the reader, and always-dark chrome.
  useEffect(() => {
    document.body.classList.add('reader-open')
    return () => document.body.classList.remove('reader-open')
  }, [])

  // Auto-hide, unless the toolbar is holding it open (cursor over a bar, or a menu is up):
  // yanking the controls out from under an open settings popover would close it mid-click.
  useEffect(() => {
    if (!chrome || chromeHeld) return
    const timer = setTimeout(() => setChrome(false), CHROME_HIDE_MS)
    return () => clearTimeout(timer)
  }, [chrome, chromeHeld, page])

  useEffect(() => {
    const onChange = () => setFullscreen(Boolean(document.fullscreenElement))
    document.addEventListener('fullscreenchange', onChange)
    return () => document.removeEventListener('fullscreenchange', onChange)
  }, [])

  const toggleFullscreen = useCallback(() => {
    if (document.fullscreenElement) {
      void document.exitFullscreen().catch(() => {})
    } else {
      void document.documentElement.requestFullscreen().catch(() => {})
    }
  }, [])

  /**
   * Moves to another chapter, flushing the current position first. `complete` is passed
   * explicitly on a forward exit so leaving the last page counts as read even when the
   * debounced write hasn't fired yet.
   */
  const goToChapter = useCallback(
    async (target: number | null, complete: boolean) => {
      if (target === null) return
      // Same gate as the position writer: before the resume lands, `page` is 0 and not a position.
      if (manifest && tracking) {
        await flushProgress(
          manifest.chapterId,
          complete ? pageCount - 1 : page,
          complete || undefined,
          // Banked time belongs to the chapter being left, and the next chapter's clock starts
          // from nothing, so it has to go out with this write or it is lost.
          clock.take(),
        ).catch(() => {})
        void queryClient.invalidateQueries({ queryKey: ['reader-progress', manifest.seriesId] })
        void queryClient.invalidateQueries({ queryKey: ['reader-continue', manifest.seriesId] })
        void queryClient.invalidateQueries({ queryKey: ['series'] })
      }
      navigate(`/read/${target}`, { replace: true })
    },
    [manifest, navigate, page, pageCount, queryClient, tracking, clock],
  )

  const next = useCallback(() => {
    const nextSpread = spreads[spreadIndex + 1]
    if (nextSpread) {
      seekToPage(nextSpread[0])
      return
    }
    if (manifest?.nextChapterId == null) return
    // Auto-advance means what it says: the page turn off the last page lands in the next chapter.
    // With it off, an interstitial instead: the chapter ends where you asked it to, and the jump
    // is a deliberate second press.
    if (prefs.autoNextChapter) void goToChapter(manifest.nextChapterId, true)
    else setAtEnd(true)
  }, [spreads, spreadIndex, manifest, prefs.autoNextChapter, goToChapter, seekToPage])

  /** Continuous mode's equivalent of `next()` hitting the chapter boundary: no spreads to check,
   *  the strip only ever has one more chapter to reach for. */
  const continuousPastEnd = useCallback(() => {
    if (manifest?.nextChapterId == null) return
    if (prefs.autoNextChapter) void goToChapter(manifest.nextChapterId, true)
    else setAtEnd(true)
  }, [manifest, prefs.autoNextChapter, goToChapter])

  const previous = useCallback(() => {
    if (atEnd) {
      setAtEnd(false)
      return
    }
    const previousSpread = spreads[spreadIndex - 1]
    if (previousSpread) {
      seekToPage(previousSpread[0])
    } else if (manifest?.previousChapterId != null) {
      void goToChapter(manifest.previousChapterId, false)
    }
  }, [spreads, spreadIndex, manifest, goToChapter, atEnd, seekToPage])

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.metaKey || event.ctrlKey || event.altKey) return
      const target = event.target as HTMLElement | null
      if (target && ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName)) return

      // In right-to-left reading the left arrow advances; in left-to-right it goes back.
      const forwardKey = prefs.direction === 'rtl' ? 'ArrowLeft' : 'ArrowRight'
      const backKey = prefs.direction === 'rtl' ? 'ArrowRight' : 'ArrowLeft'

      switch (event.key) {
        case forwardKey:
          event.preventDefault()
          next()
          break
        case backKey:
          event.preventDefault()
          previous()
          break
        case ' ':
          // Continuous mode keeps the browser's native space-to-scroll.
          if (prefs.mode !== 'vertical') {
            event.preventDefault()
            if (event.shiftKey) previous()
            else next()
          }
          break
        case 'Home':
          event.preventDefault()
          seekToPage(0)
          break
        case 'End':
          event.preventDefault()
          seekToPage(Math.max(0, pageCount - 1))
          break
        case 'f':
          toggleFullscreen()
          break
        case 'd':
          update({ direction: prefs.direction === 'rtl' ? 'ltr' : 'rtl' })
          break
        case 'b':
          toggleBookmark.mutate(page)
          break
        case 't':
          setStripOpen((open) => !open)
          break
        case '1':
          update({ mode: 'paged' })
          break
        case '2':
          update({ mode: 'double' })
          break
        case '3':
          update({ mode: 'vertical' })
          break
        case '+':
        case '=':
          setZoom((z) => Math.min(ZOOM_MAX, z + ZOOM_STEP))
          break
        case '-':
          setZoom((z) => Math.max(1, z - ZOOM_STEP))
          break
        case '0':
          setZoom(1)
          break
        case 'Escape':
          if (!document.fullscreenElement && manifest) navigate(`/series/${manifest.seriesId}`)
          break
      }
    }

    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [
    next,
    previous,
    pageCount,
    prefs,
    update,
    toggleFullscreen,
    manifest,
    navigate,
    page,
    toggleBookmark,
    seekToPage,
  ])

  /** Tap zones: outer thirds page, the middle toggles the chrome. */
  const onSurfaceClick = (event: React.MouseEvent<HTMLDivElement>) => {
    if (!prefs.tapZones || prefs.mode === 'vertical' || zoom !== 1) {
      setChrome((visible) => !visible)
      return
    }

    const bounds = event.currentTarget.getBoundingClientRect()
    const ratio = (event.clientX - bounds.left) / bounds.width
    // Right-to-left reading puts "next" on the left edge.
    const leftAdvances = prefs.direction === 'rtl'
    if (ratio < 0.33) {
      if (leftAdvances) next()
      else previous()
    } else if (ratio > 0.67) {
      if (leftAdvances) previous()
      else next()
    } else {
      setChrome((visible) => !visible)
    }
  }

  if (isLoading) {
    return (
      <div className="reader-root">
        <Center h="100vh">
          <Loader />
        </Center>
      </div>
    )
  }

  if (isError || !manifest) {
    return (
      <div className="reader-root">
        <Center h="100vh">
          <Stack align="center" gap="sm">
            <Text c="dimmed">This chapter has no readable file.</Text>
            <Button component={Link} to="/library" variant="light">
              Back to library
            </Button>
          </Stack>
        </Center>
      </div>
    )
  }

  return (
    <div className="reader-root" style={{ background: prefs.background }}>
      <ReaderToolbar
        manifest={manifest}
        page={page}
        onSeek={seekToPage}
        onPrevChapter={() => void goToChapter(manifest.previousChapterId, false)}
        onNextChapter={() => void goToChapter(manifest.nextChapterId, true)}
        prefs={prefs}
        onPrefs={update}
        selection={selection}
        onSelection={setSelection}
        source={source}
        autoProfileId={autoProfileId}
        profiles={profiles}
        fullscreen={fullscreen}
        onToggleFullscreen={toggleFullscreen}
        incognito={incognito}
        onIncognito={setIncognito}
        bookmarked={bookmarked}
        onToggleBookmark={() => toggleBookmark.mutate(page)}
        stripOpen={stripOpen}
        onToggleStrip={() => setStripOpen((open) => !open)}
        visible={chrome}
        onHold={setChromeHeld}
      />

      {atEnd ? (
        <Center h="100vh">
          <Stack align="center" gap="sm">
            <Text fz="sm" c="dimmed">
              End of {manifest.label}
            </Text>
            <Button onClick={() => void goToChapter(manifest.nextChapterId, true)}>
              Next chapter
            </Button>
            <Button variant="subtle" color="gray" onClick={() => setAtEnd(false)}>
              Stay here
            </Button>
          </Stack>
        </Center>
      ) : (
        <div className="reader-surface" onClick={onSurfaceClick}>
          {prefs.mode === 'vertical' ? (
            <ContinuousView
              urls={urls}
              page={page}
              onPageChange={setPage}
              seekVersion={seekVersion}
              onPastEnd={continuousPastEnd}
              hasNext={manifest.nextChapterId != null}
              fit={prefs.fit}
              scale={prefs.scale}
              gap={prefs.pageGap}
              label={manifest.label}
            />
          ) : (
            <PagedView
              urls={urls}
              spread={spreads[spreadIndex] ?? [0]}
              fit={prefs.fit}
              direction={prefs.direction}
              zoom={zoom}
              scale={prefs.scale}
              label={manifest.label}
              onMeasure={measure}
            />
          )}
        </div>
      )}

      {stripOpen && (
        <div
          className="reader-strip-wrap"
          data-visible={chrome}
          onMouseEnter={() => setChromeHeld(true)}
          onMouseLeave={() => setChromeHeld(false)}
        >
          <PageStrip
            urls={thumbs}
            page={page}
            bookmarks={bookmarkedPages}
            onSelect={seekToPage}
            rtl={prefs.direction === 'rtl'}
          />
        </div>
      )}

      {prefs.showPageNumber && !chrome && !atEnd && (
        <div className="reader-page-badge">
          {page + 1} / {manifest.pageCount}
        </div>
      )}
    </div>
  )
}
