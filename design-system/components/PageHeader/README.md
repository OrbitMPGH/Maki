# PageHeader

The title row at the top of a route: title and optional description on the left, actions on the right, wrapping on narrow screens.

The title sets in the display face (`page-title`: Bricolage Grotesque 700, -0.025em); the description in `ink-3` at `body` size, capped at 620px. `compact` drops the title to `type-feature` for operational pages (Queue, Activity, Logs) where the content below is the point.

**The consumer provides:** `title`, optional `description` (one sentence on what the page is for), optional `actions` (at most one filled Button, the rest `default` or `light`), `compact`.

**Don't:** hang a panel, hero band or stats beside the header. The cinematic band belongs to the series page, the Discover modal, sign-in and Rewind only.
