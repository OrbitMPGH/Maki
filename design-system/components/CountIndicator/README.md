# CountIndicator

The number on an icon button: unread notifications, downloads in progress. Mantine `Indicator` with Maki defaults, the one count shape for every icon that carries a number.

A 16px circle in the `badge` type size (0.6875rem, 700, tabular), centred on a line of 1, so a single digit is a true circle and two digits grow into a pill. Over 99 reads `99+`. It sits 5px in from the button's corner, on the glyph's corner rather than the button's, with a 2px ring in `app-bg` that cuts it off the glyph. Zero draws nothing.

| Tone | Fill | Used for |
| --- | --- | --- |
| `brand` (default) | primary | unread, in progress |
| `warn` | `warn` on `warn-on` | something waiting on you, such as a download needing an import decision |
| `danger` | `danger` on `danger-on` | failures |

**The consumer provides:** `count`, an optional `tone`, and the `ActionIcon` it decorates as the child. The button keeps its own `aria-label`; put the count in it ("Activity 3") or in a tooltip, since the circle itself is not announced.

**Not for nav rows.** Sidebar links carry their count as a trailing `.nav-count` pill in the row, not on an icon.
