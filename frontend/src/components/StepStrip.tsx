import { IconCheck } from '@tabler/icons-react'

export interface Step {
  label: string
  hint: string
  state: 'todo' | 'current' | 'done'
}

/**
 * Where a multi-step flow stands, in the setup wizard's step style laid out across a toolbar: a
 * number that turns into a check, the current step tinted. The second line says what that step
 * holds now (the root scanned, the format picked) rather than repeating its name.
 */
export function StepStrip({ steps, label }: { steps: Step[]; label: string }) {
  return (
    <ol
      className="import-steps"
      aria-label={label}
      style={steps.length === 3 ? undefined : { gridTemplateColumns: `repeat(${steps.length}, minmax(0, 1fr))` }}
    >
      {steps.map((step, i) => (
        <li
          key={step.label}
          className="import-step"
          data-state={step.state}
          aria-current={step.state === 'current' ? 'step' : undefined}
        >
          <span className="import-step-index">
            {step.state === 'done' ? <IconCheck size={13} stroke={2.6} /> : i + 1}
          </span>
          <span className="import-step-text">
            <span className="import-step-label">{step.label}</span>
            <span className="import-step-hint">{step.hint}</span>
          </span>
          {i < steps.length - 1 && <span className="import-step-thread" aria-hidden />}
        </li>
      ))}
    </ol>
  )
}
