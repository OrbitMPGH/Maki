# OverflowButton

The "more actions" trigger for a row, card or header menu: a vertical three-dot icon in a subtle `ActionIcon` with the `neutral` colour, so it stays quiet until the pointer reaches it.

It is the Mantine `ActionIcon` with `variant="subtle"`, `color="var(--neutral)"` and a 19px icon, and it forwards a ref and every other `ActionIcon` prop, so it works as a `Menu.Target`. The default `aria-label` is "More actions"; pass a more specific one ("Actions for Night reading") where several sit on one screen. Do not use it for a single obvious action: that gets its own icon button.

**The consumer provides:** the menu it opens, and an `aria-label` that names what the menu is for.
