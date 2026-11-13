# DrillControls

More pieces of the drill grammar (see DrillPanel), for pages that filter or pick many things at once. The Library's filter drawer is built from them, and the reader's "Don't enhance" page has the same shape. They sit on a drill sub-page under its back header and reuse the `drill-choice` and `drill-field` rows, so the drawer feels like the reader settings popover.

- `DrillGroup`: rows of a first page under a plain heading at `meta` size, semibold `ink-3`. It is a labelled group for a screen reader.
- `DrillMultiChoices`: any number of choices. Each row is a checkbox with a 16px box in the check slot: an outline in `border-control` when off, filled `brand` with a `brand-on` check when on. The label goes semibold `ink-hi` when on, as in `DrillChoices`.
- `DrillTriChoices`: include, leave out, or neither. One tap includes (filled box and check), a second leaves out (hollow box in `ink-3` with a minus, the label struck through in `ink-3`, and "Left out" under it), a third clears. The state is in the shape and the words, never the colour alone: included reads `aria-checked="true"` plus a hidden "Included", left out reads `aria-checked="false"` and its name ends in the visible "Left out" hint. Each box is a plain checkbox, never `mixed`, and `describedBy` links the sentence that explains the cycle to the group.
- `DrillSearch`: the `SearchInput` over a long list, inset like the rows. The list it filters renders the first hundred matches; what is picked stays listed.
- `DrillNumberRange`: a `DrillField` with two number boxes side by side. An empty box leaves that end open, so empty is never zero. A control that changes what the boxes count (Downloaded or Total chapters) goes above them as children.
- `DrillSliderRange`: a `DrillField` with two single sliders, From over To, each with its value at semibold `ink-hi` on the right. Moving one past the other pushes the other along. `DrillSliderLines` is the two sliders alone, for a field that holds another control too (My rating's Any, Rated, Unrated).
- `DrillSwitch`: an on or off setting, name and hint on the left, the switch at the end.
- `DrillFooter`: pinned under the panes when the panel fills a drawer. Reset as a subtle neutral button on the left; a live status (the result count, a `role="status"` region at `meta` size in `ink-3`) and the primary Done on the right. Done only closes: changes apply as they are made.

A first-page row whose value the user set (`DrillRow` with `active`) shows that value in `brand-fg` at semibold, so the rows that are doing something stand out. A row that runs an action instead of opening a page drops its chevron (`chevron={false}`).

**The consumer provides:** translated labels and hints, the values and their setters, the page structure through `useDrillPages` and `DrillPanes`, and for a drawer `easeHeight={false}` on `DrillPanes` so the pages scroll instead of the box growing.
