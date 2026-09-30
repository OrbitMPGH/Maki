import { useCallback, useEffect, useMemo, useState } from 'react'
import { ActionIcon, Button, Center, Group, Portal, SegmentedControl, Stack, Text } from '@mantine/core'
import { IconArrowLeft, IconX } from '@tabler/icons-react'
import { Trans, useLingui } from '@lingui/react/macro'
import { previewPageUrl, useSeriesPreview } from '../../api/preview'
import { useReaderSettings } from '../../api/reader'
import { useReadingProfiles } from '../../api/readingProfiles'
import ContinuousView from '../../pages/reader/ContinuousView'
import PagedView from '../../pages/reader/PagedView'
import { DEFAULT_PREFS } from '../../pages/reader/prefs'
import { usePreload } from '../../pages/reader/usePageUrls'

type PreviewMode = 'paged' | 'vertical'

/**
 * Reads the first chapter of a series that is not in the library, stacked over the Discover card.
 * `PreviewChapterButton` owns the server job; this only reads it.
 * Nothing here reports progress, reading time or bookmarks, and prefs changes stay in this session:
 * a preview is a look, not a read.
 */
export function SeriesPreviewReader({
  providerId,
  title,
  coverUrl,
  seriesType,
  onClose,
}: {
  providerId: string
  title: string
  coverUrl: string | null
  seriesType: string | null
  onClose: () => void
}) {
  const { t } = useLingui()
  const { data: preview, error: lostError } = useSeriesPreview(providerId, true)

  // What the user reads this type of series with elsewhere: their defaults, then the profile that
  // claims the type, the same order the real reader resolves in for a series without an override.
  const { data: settings } = useReaderSettings()
  const { data: profiles } = useReadingProfiles()
  const prefs = useMemo(() => {
    const claimed = seriesType ? profiles?.find((p) => p.seriesTypes.includes(seriesType)) : undefined
    return { ...DEFAULT_PREFS, ...settings?.defaults, ...claimed?.prefs }
  }, [settings, profiles, seriesType])
  const [modeChoice, setModeChoice] = useState<PreviewMode | null>(null)
  const mode: PreviewMode = modeChoice ?? (prefs.mode === 'vertical' ? 'vertical' : 'paged')

  const pageCount = preview?.pageCount ?? 0
  const version = preview?.version ?? null
  // Pages land roughly in order but not exactly, and a reader can only show a gap-free run.
  const readyCount = useMemo(() => {
    const ready = preview?.ready ?? []
    const gap = ready.indexOf(false)
    return gap === -1 ? ready.length : gap
  }, [preview?.ready])
  const complete = pageCount > 0 && readyCount === pageCount

  const [urls, setUrls] = useState<string[]>([])
  useEffect(() => {
    if (!version || readyCount === 0) {
      setUrls([])
      return
    }
    let cancelled = false
    void Promise.all(
      Array.from({ length: readyCount }, (_, i) => previewPageUrl(providerId, i, version)),
    ).then((resolved) => {
      if (!cancelled) setUrls(resolved)
    })
    return () => {
      cancelled = true
    }
  }, [providerId, version, readyCount])

  const [page, setPage] = useState(0)
  const [seekVersion, setSeekVersion] = useState(0)
  const [atEnd, setAtEnd] = useState(false)
  const seekToPage = useCallback((index: number) => {
    setPage(index)
    setSeekVersion((v) => v + 1)
  }, [])

  // A new version is a different source's pages, so the old position means nothing.
  useEffect(() => {
    setPage(0)
    setAtEnd(false)
  }, [version])

  usePreload(urls, page, mode === 'vertical' ? 0 : prefs.preload)

  const next = useCallback(() => {
    if (atEnd) return
    if (page + 1 < pageCount) seekToPage(page + 1)
    else if (complete) setAtEnd(true)
  }, [atEnd, page, pageCount, complete, seekToPage])

  const previous = useCallback(() => {
    if (atEnd) setAtEnd(false)
    else if (page > 0) seekToPage(page - 1)
  }, [atEnd, page, seekToPage])

  const pastEnd = useCallback(() => {
    if (complete) setAtEnd(true)
  }, [complete])

  const rtl = prefs.direction === 'rtl'
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.defaultPrevented || event.metaKey || event.ctrlKey || event.altKey) return
      const forwardKey = rtl ? 'ArrowLeft' : 'ArrowRight'
      const backKey = rtl ? 'ArrowRight' : 'ArrowLeft'
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
          if (mode !== 'vertical' || atEnd) {
            event.preventDefault()
            if (event.shiftKey) previous()
            else next()
          }
          break
        case 'Escape':
          event.preventDefault()
          event.stopPropagation()
          onClose()
          break
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [rtl, mode, atEnd, next, previous, onClose])

  const onSurfaceClick = (event: React.MouseEvent<HTMLDivElement>) => {
    if (!prefs.tapZones || mode === 'vertical') return
    const bounds = event.currentTarget.getBoundingClientRect()
    const ratio = (event.clientX - bounds.left) / bounds.width
    if (ratio < 0.33) {
      if (rtl) next()
      else previous()
    } else if (ratio > 0.67) {
      if (rtl) previous()
      else next()
    }
  }

  const failure = lostError?.message ?? (preview?.status === 'failed' ? preview.error : null)
  const source = preview?.sourceDisplayName
  const chapterLabel = preview?.chapterLabel
  const pageNumber = page + 1
  const readyPages = readyCount

  const chapterName = chapterLabel ? t`Chapter ${chapterLabel}` : t`First chapter`
  const label = `${title} · ${chapterName}`

  let body: React.ReactNode
  if (failure) {
    body = (
      <Center h="100%">
        <Stack align="center" gap="sm" maw={420} px="md">
          <Text c="rgba(255,255,255,0.72)" ta="center">
            {failure}
          </Text>
          <Button variant="light" onClick={onClose} leftSection={<IconArrowLeft size={16} />}>
            <Trans>Back to the series</Trans>
          </Button>
        </Stack>
      </Center>
    )
  } else if (atEnd) {
    body = (
      <div className="reader-end">
        {coverUrl && (
          <div className="reader-end-backdrop" style={{ backgroundImage: `url("${coverUrl}")` }} aria-hidden />
        )}
        <div className="reader-end-body">
          <div className="reader-end-finished">
            {coverUrl && <img className="reader-end-cover" src={coverUrl} alt="" />}
            <div className="reader-end-heading">
              <span className="reader-end-eyebrow">
                <Trans>End of the preview</Trans>
              </span>
              <h1 className="reader-end-title">{chapterName}</h1>
              <span className="reader-end-series">{title}</span>
            </div>
          </div>
          <div className="reader-end-next">
            <span className="reader-end-note">
              <Trans>Add the series to your library, or request it, to keep reading.</Trans>
            </span>
            <Group gap="xs" mt="md">
              <Button onClick={onClose} leftSection={<IconArrowLeft size={16} />}>
                <Trans>Back to the series</Trans>
              </Button>
              <Button variant="subtle" color="gray" className="reader-end-quiet" onClick={() => setAtEnd(false)}>
                <Trans>Stay here</Trans>
              </Button>
            </Group>
          </div>
        </div>
      </div>
    )
  } else if (urls.length === 0 || (mode === 'paged' && page >= urls.length)) {
    body = (
      <Center h="100%">
        <Stack align="center" gap="md">
          <div className="reader-page-skeleton" aria-hidden />
          <Text fz="sm" c="rgba(255,255,255,0.6)" className="tnum">
            {!source ? (
              <Trans>Looking for a source…</Trans>
            ) : pageCount > 0 ? (
              <Trans>
                Fetching from {source}, {readyPages} of {pageCount} pages
              </Trans>
            ) : (
              <Trans>Fetching from {source}…</Trans>
            )}
          </Text>
        </Stack>
      </Center>
    )
  } else {
    body = (
      <div
        className="reader-surface"
        data-scroll={mode === 'vertical' ? 'vertical' : undefined}
        onClick={onSurfaceClick}
      >
        {mode === 'vertical' ? (
          <>
            <ContinuousView
              urls={urls}
              page={page}
              onPageChange={setPage}
              seekVersion={seekVersion}
              onPastEnd={pastEnd}
              hasNext={complete}
              pastEndLabel={<Trans>Scroll to finish the preview</Trans>}
              fit={prefs.fit}
              scale={prefs.scale}
              gap={prefs.pageGap}
              label={label}
            />
            {!complete && (
              <Center py="xl">
                <div className="reader-page-skeleton" aria-hidden />
              </Center>
            )}
          </>
        ) : (
          <PagedView
            urls={urls}
            spread={[page]}
            fit={prefs.fit}
            direction={prefs.direction}
            zoom={1}
            scale={prefs.scale}
            label={label}
            onMeasure={() => {}}
          />
        )}
      </div>
    )
  }

  return (
    <Portal>
      <div className="reader-root" data-preview style={{ background: prefs.background }}>
        <div className="reader-bar reader-bar-top" data-visible onClick={(e) => e.stopPropagation()}>
          <Group gap="sm" wrap="nowrap" px="md" h="100%">
            <ActionIcon variant="subtle" color="gray" aria-label={t`Close preview`} onClick={onClose}>
              <IconX size={18} />
            </ActionIcon>
            <div style={{ minWidth: 0, flex: 1 }}>
              <Text fz="sm" fw={600} truncate>
                {title}
              </Text>
              <Text fz="xs" c="var(--ink-3)" truncate>
                {source ? (
                  <Trans>
                    Preview · {chapterName} · via {source}
                  </Trans>
                ) : (
                  <Trans>Preview</Trans>
                )}
              </Text>
            </div>
            <SegmentedControl
              size="xs"
              value={mode}
              onChange={(value) => setModeChoice(value as PreviewMode)}
              data={[
                { value: 'paged', label: t`Paged` },
                { value: 'vertical', label: t`Scroll` },
              ]}
            />
          </Group>
        </div>

        <div className="reader-preview-body">{body}</div>

        {prefs.showPageNumber && pageCount > 0 && !atEnd && !failure && (
          <div className="reader-page-badge">
            {pageNumber} / {pageCount}
          </div>
        )}
      </div>
    </Portal>
  )
}
