import type { ReactNode } from 'react'
import { UnstyledButton } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { useLabel } from '../i18n-context'
import { IconCheck } from '../lib/icons'
import { onRovingKeyDown } from '../lib/rovingFocus'
import { useThemeChoice, type BackgroundOption } from '../theme-context'

/**
 * Background preview tiles, then the accent row. Shared by Settings, the setup wizard and the
 * appearance announcement, which passes `backgroundHints` to tag backgrounds by id.
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
  const groups: { key: string; label: string; items: BackgroundOption[] }[] = [
    { key: 'dark', label: t`Dark`, items: backgrounds.filter((b) => b.scheme === 'dark') },
    { key: 'light', label: t`Light`, items: backgrounds.filter((b) => b.scheme !== 'dark') },
  ]

  return (
    <div className="appearance-picker">
      <div>
        <span className="appearance-picker-label">
          <Trans>Background</Trans>
        </span>
        {/* One flat grid so the radios stay direct children for arrow-key roving; group labels span a row. */}
        <div className="ground-tiles" role="radiogroup" aria-label={t`Background`} onKeyDown={onRovingKeyDown}>
          {groups.map((group) => [
            <span key={group.key} className="ground-tiles-group">
              {group.label}
            </span>,
            ...group.items.map((b) => {
              const active = b.id === background
              const hint = backgroundHints?.[b.id]
              const p = b.preview(accentHex)
              return (
                <UnstyledButton
                  key={b.id}
                  role="radio"
                  aria-checked={active}
                  tabIndex={active ? 0 : -1}
                  className="ground-tile"
                  data-active={active || undefined}
                  onClick={() => setBackground(b.id)}
                >
                  <span className="ground-tile-screen" style={{ background: p.ground }} aria-hidden>
                    {p.side && (
                      <span
                        className="ground-tile-side"
                        style={{ background: p.side, borderInlineEnd: p.sideEdge && `1px solid ${p.sideEdge}` }}
                      />
                    )}
                    <span
                      className="ground-tile-card"
                      data-full={p.side ? undefined : true}
                      style={{ background: p.surface, borderColor: p.border }}
                    >
                      <i style={{ background: p.accent, width: '40%' }} />
                      {p.ink && <i style={{ background: p.ink, width: '80%', opacity: 0.5 }} />}
                      {p.ink && <i style={{ background: p.ink, width: '60%', opacity: 0.3 }} />}
                    </span>
                  </span>
                  <span className="ground-tile-name">
                    {active && (
                      <span className="ground-tile-check" aria-hidden>
                        <IconCheck size={10} />
                      </span>
                    )}
                    {renderLabel(b.label)}
                  </span>
                  {hint && (
                    <span className="setup-swatch-hint" data-muted={hint.muted || undefined}>
                      {hint.label}
                    </span>
                  )}
                </UnstyledButton>
              )
            }),
          ])}
        </div>
      </div>
      <div>
        <span className="appearance-picker-label">
          <Trans>Accent</Trans>
        </span>
        <div className="setup-swatches" role="radiogroup" aria-label={t`Accent`} onKeyDown={onRovingKeyDown}>
          {accents.map((a) => {
            const active = a.id === accent
            return (
              <UnstyledButton
                key={a.id}
                role="radio"
                aria-checked={active}
                tabIndex={active ? 0 : -1}
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
