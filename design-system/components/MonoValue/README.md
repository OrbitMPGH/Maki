# MonoValue

A machine string (file name, folder path, source series id) in the monospace face, on one line.

It never wraps. When its column is too narrow it gives up the middle: the head ellipsizes and the tail stays put. Release names carry the volume or chapter number in the middle and tags at the end, so the tail starts at the first number token (`v05`, `Ch.48`, `c048`) that leaves at most 32 characters, and otherwise is the extension and eight characters before it, or the last six characters of an id or path. `Kubo Won't Let Me Be Invisible v05 c048.cbz` reads as `Kubo Won't Let…v05 c048.cbz` rather than breaking one character per line. Hovering a clipped value shows the whole thing in a tooltip. With `copy`, a small copy button sits after it, shown on hover of the value or its table row and always on touch screens.

A table holding one needs a minimum width and scrolls as a whole (`Table.ScrollContainer`), and the value takes `minWidth` so its column cannot shrink to the tail. Used for the series Files tab, the series folder, source ids, Health's archive paths and root folders. Confirmation dialogs that name a path someone is about to delete keep the full path instead.

**The consumer provides:** `value`, optional `full` (what the tooltip shows and the button copies when the label is shorter), `href` for a link, `copy`, `minWidth`, and `fz`/`c` for size and colour.
