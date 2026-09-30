import type { ReactNode } from 'react'
import { UnstyledButton } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { useLabel } from '../i18n-context'
import { useThemeChoice } from '../theme-context'

/**
 * Two swatch rows, background then accent. Shared by Settings, the setup wizard and the appearance
 * announcement, which passes `backgroundHints` to tag swatches by id.
 */
export function AppearancePicker({
  backgroundHints,
}: {
  backgroundHints?: Record<string, { label: ReactNode; muted?: boolean }>
}) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const { background, setBackground, accent, setAccent, backgrounds, accents } = useThemeChoice()
  const accentHex = accents.find((a) => a.id === accent)?.swatch ?? accents[0].swatch

  return (
    <div className="appearance-picker">
      <div>
        <span className="appearance-picker-label">
          <Trans>Background</Trans>
        </span>
        <div className="setup-swatches" role="radiogroup" aria-label={t`Background`}>
          {backgrounds.map((b) => {
            const active = b.id === background
            const hint = backgroundHints?.[b.id]
            return (
              <UnstyledButton
                key={b.id}
                role="radio"
                aria-checked={active}
                className="setup-swatch"
                data-active={active || undefined}
                onClick={() => setBackground(b.id)}
              >
                <span className="setup-swatch-dot" style={{ background: b.swatch(accentHex) }} />
                <span>{renderLabel(b.label)}</span>
                {hint && (
                  <span className="setup-swatch-hint" data-muted={hint.muted || undefined}>
                    {hint.label}
                  </span>
                )}
              </UnstyledButton>
            )
          })}
        </div>
      </div>
      <div>
        <span className="appearance-picker-label">
          <Trans>Accent</Trans>
        </span>
        <div className="setup-swatches" role="radiogroup" aria-label={t`Accent`}>
          {accents.map((a) => {
            const active = a.id === accent
            return (
              <UnstyledButton
                key={a.id}
                role="radio"
                aria-checked={active}
                className="setup-swatch"
                data-active={active || undefined}
                onClick={() => setAccent(a.id)}
              >
                <span className="setup-swatch-dot" style={{ background: a.swatch }} />
                <span>{renderLabel(a.label)}</span>
              </UnstyledButton>
            )
          })}
        </div>
      </div>
    </div>
  )
}
