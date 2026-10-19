# Panel

The house surface: a bordered Mantine `Paper` at `radius-lg` (13px), `spacing-lg` padding, on `surface`, with the 1px `shadow-card` in both schemes.

No accent edges. A panel that needs to report state puts a `StatusDot` beside its title (Health's checks panel shows how many checks need attention in `warn` or `danger`).

**The consumer provides:** children and any Paper prop. Inside an operational `SurfaceFrame` the padding drops to `spacing-md`.

**Layers:** use `.layer-raised` (`surface-raised`, `shadow-raised`) for something that floats, `.layer-sunken` (`surface-sunken`) for a recessed well, `.layer-flush` for dense rows that should read as page, not card.
