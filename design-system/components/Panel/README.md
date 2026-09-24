# Panel

The house surface: a bordered Mantine `Paper` at `radius-lg` (13px), `spacing-lg` padding, no shadow, on `surface`.

An optional 2px accent `edge` stands in for a shadow when a panel needs to say something: `brand` for the featured or connected item, `info`, `ok`, `warn`, `danger` for state, `strong` for a neutral emphasis in `border-strong`. `edgeSide="top"` keeps all four borders; `left` drops the right border.

**The consumer provides:** children, optional `edge` and `edgeSide`, and any Paper prop. Inside an operational `SurfaceFrame` the padding drops to `spacing-md`.

**Layers:** use `.layer-raised` (`surface-raised`, `shadow-raised`) for something that floats, `.layer-sunken` (`surface-sunken`) for a recessed well, `.layer-flush` for dense rows that should read as page, not card.
