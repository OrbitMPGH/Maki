# Callout

A tinted box for something the page wants noticed: a tone icon, the text, and optional buttons at the end that wrap under the text on a narrow screen. Use `Note` when one line needs no box.

Tones: `warn` (default), `info`, `danger`, `ok`, `neutral`, `brand`. Each tone supplies its own icon, border and tint from the status tokens, so a warning is the same `warn` on a Callout, a Badge and a Health row. An optional `title` sits above the body in a heavier weight. Pass `role="alert"` for something that appears after an action failed and `role="status"` for something that appears while the user waits; leave it off for static content.

**The consumer provides:** the text (translated), `tone`, and optionally `title`, `actions` (buttons) and `icon`.
