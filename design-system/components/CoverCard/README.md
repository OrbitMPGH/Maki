# CoverCard

The poster card for the library grid: cover art is the hero, with a bottom scrim carrying the title, a download-progress bar and a have/total count.

The 2:3 poster uses `radius-cover`, a translucent `cover-rim` edge and `shadow-cover`. Its low scrim takes a tint from the cover when the server provides one. On hover the poster lifts 4px and the art grows to 105%. Under reduced motion it stays still. In bulk mode the card is a checkbox and selection adds a tick, a `brand` border and a 2px ring.

**Corners.** The top left holds a download in progress or a saved-offline mark. The publication status disc sits at the top right and is hollow for an unmonitored series. In Ring mode a `brand` arc wraps that same disc and closes when all downloaded chapters are read. The eye-off and muted bell appear beside it on hover or keyboard focus and stay visible on touch.

The Library menu is a sibling of the card link. It shows at the top left on pointer hover or keyboard focus and stays at the right end of the count row on touch. Bulk and offline modes hide it.

**Footer.** The two-line display title sits above the count row and progress bar. A neutral “N new” pill counts new chapters to read and takes priority over the download count on narrow cards. The download fill turns from `brand` to `ok` when complete. In Bar mode reading progress is drawn over the downloaded fill against the series total, turning `ok` once the entire series is read. With no chapters or total, the card says “No chapters” and reserves space for the hidden bar.

Pass `series`, `selectMode`, `selected`, `readTracking` and `onToggle(id, shiftKey?)`. Optional `menu` enables the Library menu, and `offline` marks saved or unavailable titles. The grid uses plain markup and the shared tooltip layer so hundreds of cards can mount cheaply.
