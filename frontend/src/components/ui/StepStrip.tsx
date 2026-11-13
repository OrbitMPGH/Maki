import { UnstyledButton } from '@mantine/core'
import { IconCheck } from '../../lib/icons'

export interface Step {
  label: string
  hint?: string
  state: 'todo' | 'current' | 'done'
  tone?: 'warn'
  disabled?: boolean
}

interface StepStripProps {
  steps: Step[]
  label: string
  layout?: 'strip' | 'rail'
  onSelect?: (index: number) => void
}

/**
 * Where a multi-step flow stands: a number that turns into a check, the current step in the
 * accent, finished steps green. The optional second line says what the step holds now rather than
 * repeating its name. Pass `onSelect` to make the steps buttons; `layout="rail"` stacks them.
 */
export function StepStrip({ steps, label, layout = 'strip', onSelect }: StepStripProps) {
  const rail = layout === 'rail'
  return (
    <ol
      className="step-strip"
      data-layout={layout}
      data-many={steps.length > 3 || undefined}
      aria-label={label}
      style={rail ? undefined : { gridTemplateColumns: `repeat(${steps.length}, minmax(0, 1fr))` }}
    >
      {steps.map((step, i) => {
        const body = (
          <>
            <span className="step-strip-index">
              {step.state === 'done' ? <IconCheck size={13} /> : i + 1}
            </span>
            <span className="step-strip-text">
              <span className="step-strip-label">{step.label}</span>
              {step.hint && (
                <span className="step-strip-hint" data-tone={step.tone}>
                  {step.hint}
                </span>
              )}
            </span>
            {!rail && i < steps.length - 1 && <span className="step-strip-thread" aria-hidden />}
          </>
        )
        const current = step.state === 'current' ? 'step' : undefined
        return (
          <li key={step.label}>
            {onSelect ? (
              <UnstyledButton
                className="step-strip-item"
                data-state={step.state}
                aria-current={current}
                disabled={step.disabled}
                onClick={() => onSelect(i)}
              >
                {body}
              </UnstyledButton>
            ) : (
              <div className="step-strip-item" data-state={step.state} aria-current={current}>
                {body}
              </div>
            )}
          </li>
        )
      })}
    </ol>
  )
}
