# Button

Mantine `Button` with Maki's theme defaults: `radius="md"` (9px), weight 600, primary fill from `primary` with its label in `primary-on` (Mantine autoContrast: black on Emerald and Amber).

**Variants, by how often the app reaches for them:**
- `light` (most common): secondary actions that still belong to the brand, like Refresh or Search. Tinted brand fill; the label is `brand-fg`.
- `subtle`: tertiary and inline actions, especially the "Find more" action at the end of a `SectionHeader` rule.
- `default`: neutral actions (Cancel, Browse, the single action inside an `EmptyState`). `surface-2` fill, `border-strong` outline.
- `filled` (Mantine's default when no variant is set): the primary action on a screen (Add series, Save).

**The consumer provides:** a label that says what happens ("Add series", not "OK"), an optional Tabler icon at 16px as `leftSection`, and `size` (`xs` for dense toolbars, default `sm`, `md` for forms).

**Don't:** reach for Mantine's stock palette. Colour props take a status token: filled destructive buttons pass `color="var(--danger-fill)"` and confirm through `ConfirmDialog`, lighter variants pass `color="var(--danger)"`; quiet ones pass `color="var(--neutral)"`. Never put two filled buttons side by side.

The preview is a static rendition styled by `bundle.css`; in the app this is Mantine.
