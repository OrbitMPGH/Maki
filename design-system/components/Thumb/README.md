# Thumb

The small cover for rows, lists and tables: 2:3, one corner radius per size, the cover rim as an inset 1px line, and a brand-tinted tile when there is no cover. Poster-sized covers use `CoverCard`.

| Size | Width | Radius |
| --- | --- | --- |
| `sm` | 28px | `radius-xs` |
| `md` (default) | 36px | `radius-control` |
| `lg` | 56px | `radius-thumb` |

`width` overrides the size's width in px; the ratio and the radius stay. The rim is drawn over the image by a pseudo-element, so a cover with a pale edge still has a boundary on a light theme. A missing or broken cover shows the brand tile (`brand` mixed into `app-bg`), the same tile the cover fallback swaps in.

**The consumer provides:** `src` (or nothing for the tile), `alt` (empty when the title sits beside it), `size`, and optionally `width`.
