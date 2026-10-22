import { useEffect, useState, type CSSProperties, type ReactNode } from 'react'
import { Paper, Text, Title } from '@mantine/core'
import { useLingui } from '@lingui/react/macro'
import { getInitialize } from '../../api/client'
import { IconBrandMark } from '../IconBrandMark'

/** Accent strength of each shape in the band: fixed, so the art is the same on every visit. */
const SHAPES = Array.from({ length: 50 }, (_, i) => 10 + ((i * 37) % 62))

/**
 * The library mosaic's address when an admin has switched it on, else null. Null on any failure
 * too: an offline start or a broken bootstrap still gets the page's own art.
 */
function useCoverMosaicUrl(): string | null {
  const [url, setUrl] = useState<string | null>(null)
  useEffect(() => {
    let live = true
    getInitialize()
      .then((init) => {
        if (live && init.signInCovers) setUrl(`${init.apiRoot}/signin/mosaic.webp`)
      })
      .catch(() => {})
    return () => {
      live = false
    }
  }, [])
  return url
}

/**
 * The frame behind sign-in and first-run setup, with the page's one cinematic band.
 * <p>
 * By default the band is Maki's own art (cover shapes in the accent colour) and the mascot is drawn
 * at size: nobody is signed in, and fetching covers anonymously would show what the instance holds
 * to anyone who can reach the form. An admin can opt in to a mosaic of Safe-rated covers, which the
 * server blurs into one image; it replaces the shapes once it has loaded, and the tagline goes,
 * since the covers say it.
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
  const mosaicUrl = useCoverMosaicUrl()
  const [covers, setCovers] = useState(false)

  return (
    <div className="auth-frame" data-art={covers ? 'covers' : 'shapes'}>
      <div className="auth-band" aria-hidden>
        <div className="auth-band-shapes">
          {SHAPES.map((shade, i) => (
            <span key={i} style={{ '--shade': `${shade}%` } as CSSProperties} />
          ))}
        </div>
        {mosaicUrl && (
          <img
            className="auth-band-covers"
            src={mosaicUrl}
            alt=""
            decoding="async"
            onLoad={() => setCovers(true)}
            onError={() => setCovers(false)}
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
