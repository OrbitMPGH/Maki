import { Button, Center, Loader, Stack, Text } from '@mantine/core'
import { useQueryClient } from '@tanstack/react-query'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
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
import { spreadIndexOf, usePageAspects, useSpreads } from './useSpreads'

const ZOOM_STEP = 0.25
const ZOOM_MAX = 4

/**
 * The chromeless reader. Rendered outside the AppShell (see App.tsx) so it owns the whole
 * viewport, and always dark regardless of the theme preset — the same choice the Rewind overlay
 * makes, because page art has to sit on neutral black.
 */
export default function ReaderPage() {
  const { chapterId: param } = useParams()
  const chapterId = Number(param)
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const { data: manifest, isLoading, isError } = useReaderManifest(chapterId)
  const { prefs, update, scope, setScope } = useReaderPrefs(manifest)

  const [page, setPage] = useState(0)
  const [chrome, setChrome] = useState(true)
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
  useReaderProgress(manifest?.chapterId, page, Boolean(manifest) && !incognito)

  // Resume where the chapter was left off, once per chapter.
  const resumedFor = useRef<number | null>(null)
  useEffect(() => {
    if (manifest && resumedFor.current !== manifest.chapterId) {
      resumedFor.current = manifest.chapterId
      setPage(manifest.resumePage)
      setZoom(1)
      setAtEnd(false)
    }
  }, [manifest])

  // Own the viewport: no page scrolling behind the reader, and always-dark chrome.
  useEffect(() => {
    document.body.classList.add('reader-open')
    return () => document.body.classList.remove('reader-open')
  }, [])

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
      if (manifest && !incognito) {
        await flushProgress(
          manifest.chapterId,
          complete ? pageCount - 1 : page,
          complete || undefined,
        ).catch(() => {})
        void queryClient.invalidateQueries({ queryKey: ['reader-progress', manifest.seriesId] })
        void queryClient.invalidateQueries({ queryKey: ['reader-continue', manifest.seriesId] })
        void queryClient.invalidateQueries({ queryKey: ['series'] })
      }
      navigate(`/read/${target}`, { replace: true })
    },
    [manifest, navigate, page, pageCount, queryClient, incognito],
  )

  const next = useCallback(() => {
    const nextSpread = spreads[spreadIndex + 1]
    if (nextSpread) {
      setPage(nextSpread[0])
      return
    }
    if (manifest?.nextChapterId == null) return
    // An interstitial rather than an immediate jump: sliding straight into the next chapter mid
    // page-turn is disorienting, and it's the only chance to say there is nothing left.
    if (prefs.autoNextChapter) setAtEnd(true)
  }, [spreads, spreadIndex, manifest, prefs.autoNextChapter])

  const previous = useCallback(() => {
    if (atEnd) {
      setAtEnd(false)
      return
    }
    const previousSpread = spreads[spreadIndex - 1]
    if (previousSpread) {
      setPage(previousSpread[0])
    } else if (manifest?.previousChapterId != null) {
      void goToChapter(manifest.previousChapterId, false)
    }
  }, [spreads, spreadIndex, manifest, goToChapter, atEnd])

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
          setPage(0)
          break
        case 'End':
          event.preventDefault()
          setPage(Math.max(0, pageCount - 1))
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
            <Button component={Link} to="/" variant="light">
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
        onSeek={setPage}
        onPrevChapter={() => void goToChapter(manifest.previousChapterId, false)}
        onNextChapter={() => void goToChapter(manifest.nextChapterId, true)}
        prefs={prefs}
        onPrefs={update}
        scope={scope}
        onScope={setScope}
        fullscreen={fullscreen}
        onToggleFullscreen={toggleFullscreen}
        incognito={incognito}
        onIncognito={setIncognito}
        bookmarked={bookmarked}
        onToggleBookmark={() => toggleBookmark.mutate(page)}
        stripOpen={stripOpen}
        onToggleStrip={() => setStripOpen((open) => !open)}
        visible={chrome}
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
              fit={prefs.fit}
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
              label={manifest.label}
              onMeasure={measure}
            />
          )}
        </div>
      )}

      {stripOpen && (
        <div className="reader-strip-wrap" data-visible={chrome}>
          <PageStrip
            urls={thumbs}
            page={page}
            bookmarks={bookmarkedPages}
            onSelect={setPage}
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
