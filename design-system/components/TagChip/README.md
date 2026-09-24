# TagChip

The bordered tag pill: `surface-2` fill, `border-strong` outline, `radius-chip` (7px, squarer than a pill), `ink-2` text at `meta` size.

A `dot` adds the 6px bucket dot in any colour (genre buckets, content ratings). `active` fills it with brand at 35% and turns the dot brand. Give it `onClick` or `href` and it becomes a button or link with a hover fill and a 2px `brand` focus outline; without either it stays a plain span, so a row of labels is not a row of controls.

**The consumer provides:** the label, optional `dot`, `active`, `onClick` or `href`, `size` (`sm` for dense rows). Wrap a run of chips in `TagChips` (flex-wrap, 7px gap).
