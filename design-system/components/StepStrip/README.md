# StepStrip

Where a multi-step flow stands: each step is a number that turns into a check when done, the current step is in the accent, finished steps are green. A thread runs between steps in the strip layout. The optional second line (`hint`) says what the step holds now ("3 folders") rather than repeating its name; a hint with `tone: 'warn'` shows in `warn` for a step that needs attention.

Two layouts. `strip` (default) is a row of equal columns, one per step, collapsing to numbers and labels on a narrow screen. `rail` stacks the steps vertically with a line down the side, for a flow that sits beside its content. Pass `onSelect` and the steps become buttons (a disabled step is skipped); without it they are plain items and the strip only reports. The list is an `<ol>` named by `label`, and the current step has `aria-current="step"`.

**The consumer provides:** `steps` (`label`, `state` of `todo`, `current` or `done`, and optionally `hint`, `tone`, `disabled`), a `label` for the list, `layout` and `onSelect`.
