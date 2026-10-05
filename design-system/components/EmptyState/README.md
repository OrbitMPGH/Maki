# EmptyState

What a section says when it has nothing to show: a plain statement, one line on what would fill it, and at most one thing to do about it.

In a section it sits left-aligned with the content it stands in for, never a centred icon card. The title is `section` style in `ink-hi`; the description `ink-3`, capped at 520px; the action a `default` Button. `compact` is for a section of an operational page where empty is the normal state: the title drops to `body` size in `ink-2` and the padding shrinks.

`mood` (`asleep`, `pleased` or `asking`) sets the mascot beside the text of the inline form, 56px, vertically centred with a 16px gap; the text stays left-aligned and `compact` padding still applies. Only the eyes and mouth change, the body and colours are the fixed mark. Use `asleep` for an empty queue or list, `pleased` when there is nothing left to deal with (an empty notifications inbox), `asking` when the state needs something from the reader. Leave it off error states ("Couldn't load ...") and off `art` states.

When the whole page is empty (an empty library, a series or route that doesn't exist) pass `art`, and the state centres itself in the page over a picture of covers: `shelf` is a row of dashed cover slots fading out from a brand-tinted middle one with a plus, `missing` a lone tilted blank cover with a question mark and an optional `code` (the id or path that led nowhere). The title moves to the display face at `feature` size, the action becomes a filled `md` Button, and a second `default` Button may sit beside it. Never use `art` inside a section.

**The consumer provides:** `title` ("No series yet", a statement, not an apology), optional `description`, optional `actionLabel` with `actionTo` or `onAction`, `compact`, optional `mood`. For a page: `art`, optional `code`, `headingOrder` (1 when the page has no header of its own), `actionIcon`, and `secondaryActionLabel` with `secondaryActionTo` and `secondaryActionIcon`.
