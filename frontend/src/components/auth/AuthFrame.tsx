import { useCallback, useEffect, useRef, useState, type CSSProperties, type ReactNode } from 'react'
import { Paper, Text, Title } from '@mantine/core'
import { useLingui } from '@lingui/react/macro'
import { getInitialize } from '../../api/client'
import { IconBrandMark } from '../IconBrandMark'

/** Accent strength of each shape in the band: fixed, so the art is the same on every visit. */
const SHAPES = Array.from({ length: 50 }, (_, i) => 10 + ((i * 37) % 62))

type ArtMode = 'pending' | 'shapes' | 'covers'

const ART_KEY = 'auth-art'

/** How long the frame waits for the bootstrap to answer before it paints its own art. */
const ART_PENDING_MS = 1500

/** How long it then waits for the mosaic image once the bootstrap has said covers are on. */
const ART_COVERS_MS = 5000

function readArt(): ArtMode {
  try {
    const stored = localStorage.getItem(ART_KEY)
    if (stored === 'shapes' || stored === 'covers') return stored
  } catch {
    /* private mode: the first paint waits for the server */
  }
  return 'pending'
}

function writeArt(mode: 'shapes' | 'covers') {
  try {
    localStorage.setItem(ART_KEY, mode)
  } catch {
    /* private mode: remembered for this visit only */
  }
}

/**
 * Which art the band shows. Starts from the last answer so a returning visitor paints the right
 * mode at once; a first visit holds at "pending" (neither art, no identity block) until the
 * bootstrap and, when covers are on, the mosaic have answered, so the layout never jumps from one
 * mode to the other. Waiting is bounded: past the timeouts the page's own art shows. Once shapes
 * have been painted in this visit the mosaic is remembered for the next one but never swapped in,
 * and a failed mosaic load falls back to shapes without being remembered.
 */
function useAuthArt() {
  const [mode, setMode] = useState<ArtMode>(readArt)
  const [mosaicUrl, setMosaicUrl] = useState<string | null>(null)
  const shapesShown = useRef(mode === 'shapes')

  const showShapes = useCallback(() => {
    setMode((m) => {
      if (m === 'pending') shapesShown.current = true
      return m === 'pending' ? 'shapes' : m
    })
  }, [])

  useEffect(() => {
    let live = true
    let coversExpected = false
    const timers: number[] = []
    getInitialize()
      .then((init) => {
        if (!live) return
        if (init.signInCovers) {
          coversExpected = true
          setMosaicUrl(`${init.apiRoot}/signin/mosaic.webp`)
          timers.push(window.setTimeout(showShapes, ART_COVERS_MS))
        } else {
          shapesShown.current = true
          setMode('shapes')
          writeArt('shapes')
        }
      })
      .catch(() => {
        if (!live) return
        shapesShown.current = true
        setMode('shapes')
      })
    timers.push(
      window.setTimeout(() => {
        if (!coversExpected) showShapes()
      }, ART_PENDING_MS),
    )
    return () => {
      live = false
      timers.forEach((id) => window.clearTimeout(id))
    }
  }, [showShapes])

  return {
    mode,
    mosaicUrl,
    onCoversLoad: () => {
      writeArt('covers')
      if (!shapesShown.current) setMode('covers')
    },
    onCoversError: () => {
      shapesShown.current = true
      setMode('shapes')
    },
  }
}

/**
 * The frame behind sign-in and first-run setup, with the page's one cinematic band.
 * <p>
 * By default the band is Maki's own art (cover shapes in the accent colour) and the mascot is drawn
 * at size: nobody is signed in, and fetching covers anonymously would show what the instance holds
 * to anyone who can reach the form. An admin can opt in to a mosaic of Safe-rated covers, which the
 * server blurs into one image; it replaces the shapes once it has loaded, and the tagline goes,
 * since the covers say it. Until the mode is known (see {@link useAuthArt}) neither is painted.
 */
export function AuthFrame({
  title,
  subtitle,
  children,
  footer,
}: {
  title: string
  subtitle?: ReactNode
  children: ReactNode
  footer?: ReactNode
}) {
  const { t } = useLingui()
  const { mode, mosaicUrl, onCoversLoad, onCoversError } = useAuthArt()

  return (
    <div className="auth-frame" data-art={mode}>
      <div className="auth-band" aria-hidden>
        <div className="auth-band-shapes">
          {SHAPES.map((shade, i) => (
            <span key={i} style={{ '--shade': `${shade}%` } as CSSProperties} />
          ))}
        </div>
        {mosaicUrl && (
          <img
            className="auth-band-covers"
            data-no-cover-fallback
            src={mosaicUrl}
            alt=""
            decoding="async"
            onLoad={onCoversLoad}
            onError={onCoversError}
          />
        )}
        <div className="auth-band-scrim" />
      </div>

      <div className="auth-content">
        <div className="auth-identity">
          <span className="brand-mark auth-brand-mark">
            <IconBrandMark />
          </span>
          <h1 className="auth-wordmark">Maki</h1>
          <Text className="auth-tagline">{t`Self-hosted manga library and downloader.`}</Text>
        </div>

        <div className="auth-column">
          <Paper withBorder radius="lg" p="lg" className="series-hero-panel auth-panel">
            <Title order={2} className="auth-panel-title">
              {title}
            </Title>
            {subtitle && (
              <Text fz="sm" c="var(--ink-3)" mt={6}>
                {subtitle}
              </Text>
            )}
            <div className="auth-panel-body">{children}</div>
          </Paper>
          {footer}
        </div>
      </div>
    </div>
  )
}

/** The one error shape both auth screens use: a slab, not a Mantine Alert with its own palette. */
export function AuthError({ children }: { children: ReactNode }) {
  return (
    <Text role="alert" className="auth-error">
      {children}
    </Text>
  )
}
