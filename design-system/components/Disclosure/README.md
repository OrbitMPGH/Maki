# Disclosure

A heading row that opens the controls under it. For a surface that keeps every control but needs only a few on screen at once: the library's filter groups and Health's thresholds.

The title is `body` size at semibold `ink-hi`, the chevron turns when open, and rows in a run share one `border` hairline between them so a stack of groups reads as one list. `count` sets a `primary` pill with its number in `primary-on` beside the title, so a closed group still shows it is doing something. `summary` is up to two lines of `ink-3` at `meta` size under the title, shown only while closed: say what is inside or what it is set to, never repeat the title. When the summary reports active settings it reads in `ink-2` instead (`disclosure-summary-active`). The body stays mounted when closed, so a half-typed edit survives a collapse.

**The consumer provides:** `title`, `opened` and `onToggle`, the body as children, optional `count` with a translated `countLabel` for screen readers, optional `summary`.
