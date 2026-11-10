# DrillPanel

The grammar of a small settings panel that opens in place: the reader settings popover, the series page's "More actions" menu and the add card in the Discover detail modal. A first page of rows, each showing its current value, and sub-pages that slide in from the right with a back header. Nothing on it opens a floating dropdown: a choice is made on a page of its own or on a segmented control.

- `DrillRow`: the setting's icon in `brand`, its name in `ink`, its current value at `meta` size in `ink-3` (truncated), and a chevron in `ink-3`. The whole row is the button and takes `surface-hover` on hover. The value is what makes the first page useful on its own: a glance says where a series will go and whether it stays private, without opening anything.
- `DrillBack`: the sub-page header. A 26px back square, then the page's name at semibold `ink-hi`. Its accessible name reads "Back" and the page.
- `DrillChoices`: one choice from a list. Each option is a row with a `brand` check on the current one, its label (semibold `ink-hi` when chosen) and a hint under it at `meta` size in `ink-3` saying what picking it does. Picking an option goes back to the page that opened it.
- `DrillField`: a setting changed in place. Its icon in `brand` and name over the control, usually a `size="xs"` SegmentedControl, and one line under it in `ink-3` describing the current pick, so the line changes as the selection does.
- `DrillPanes` slides between pages (a deeper page from the right, a shallower one from the left) and eases the panel's height to the new page. `useDrillPages` keeps the page and moves focus: to the back header on the way in, to the row that opened the page on the way out.

**When to use which.** A short, exclusive choice (up to four short labels) whose options need no explaining beyond the current one: a `DrillField` with a segmented control. A longer list, or one where each option needs its own sentence (folders with their free space, profiles with their descriptions, modes with what they do): a `DrillRow` that opens `DrillChoices`. Put the settings people check every time on the first page and tuck what is usually left alone one level down. The panel's primary action (Add to library, say) stays outside the panes so it never slides away.

**The consumer provides:** the pages and their parents for `useDrillPages`, one `DrillPanes` keyed on the current page, and on each page its rows, choices or fields with translated labels and hints.
