import type { ReactNode } from 'react'
import { Paper, Text, Title } from '@mantine/core'
import { useLingui } from '@lingui/react/macro'
import { IconBrandMark } from '../IconBrandMark'
import { HeroBackdrop } from '../series/HeroBackdrop'

/**
 * The band behind sign-in and first-run setup, so the first screen anyone sees belongs to the same
 * product as the series page.
 *
 * The art is CSS rather than a cover: nobody is signed in yet, and fetching library art anonymously
 * would leak what the instance holds to anyone who can reach the login form. The three scrim layers
 * are the hero's own, rendered with no image behind them.
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

  return (
    <div className="auth-frame">
      <div className="auth-backdrop-art" aria-hidden />
      <HeroBackdrop coverUrl={null} />

      <div className="auth-content">
        <div className="auth-identity">
          <span className="brand-mark auth-brand-mark">
            <IconBrandMark />
          </span>
          <h1 className="auth-wordmark">Maki</h1>
          <Text className="auth-tagline">{t`Self-hosted manga library and downloader.`}</Text>
        </div>

        <div className="auth-column">
          <Paper withBorder radius="lg" p="lg" className="series-hero-glass-panel auth-panel">
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
