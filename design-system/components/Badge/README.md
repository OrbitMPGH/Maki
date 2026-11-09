# Badge

Mantine `Badge` with Maki defaults: `radius="sm"` (6px), `fw={600}`, almost always `variant="light"`.

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
