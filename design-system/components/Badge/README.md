# Badge

Mantine `Badge` and `LightBadge` share the same treatment. The small light badge uses `radius-sm`, `type-badge` and a weight of 600. A tint fills the badge and a 1px edge uses the foreground at 45%. Use the matching semantic tone and a translated label. The light danger variant uses `danger-fg` for the label and edge.

A badge names a state in a word. The colour comes from the status tokens, never Mantine's stock palette: map a status through `statusColor()` (status.tsx) so "Completed" is the same `ok` green on a badge, a cover and a table row, and follows the light theme.

| Status | Token |
| --- | --- |
| Ongoing, in progress | `info` |
| Completed, success | `ok` |
| Hiatus, partial | `warn` |
| Cancelled, failed | `danger` |
| Queued, skipped | `neutral` |
| Needs review, paused, missing file | `warn` |
| Anime, watched | `anime` |
| Volume | `volume` |
| Content rating | `cr-safe`, `cr-suggestive`, `cr-erotica`, `cr-explicit` |
| Quality tier | `q-official`, `q-scanlator`, `q-aggregator` |
| Unknown | `neutral` |

**The consumer provides:** the word (translated) and a status. **In a dense table use `StatusDot` instead:** a filled badge on every row turns the column into a stripe.
