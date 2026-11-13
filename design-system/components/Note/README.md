# Note

One quiet line with an icon in the tone's colour, for where a boxed `Callout` would outweigh the content: the bottom of a dialog, a settings panel, a Health row. The text is `ink-2` at `meta` size; only the icon carries the tone.

Tones: `warn` (default), `info`, `danger`, `ok`, `neutral`. `danger` has `role="alert"` and `warn` has `role="status"`. An `action` sits at the end of the line and wraps under the text when it does not fit; `closeLabel` with `onClose` adds a small dismiss button. `mt` and `mb` add the standard margin above (after panel content) or below (above a page body), so call sites do not hand-roll spacing.

**The consumer provides:** the text (translated) and `tone`, and optionally `action`, `closeLabel` and `onClose`.
