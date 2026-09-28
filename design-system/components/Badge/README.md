# Badge

Mantine `Badge` with Maki defaults: `radius="sm"` (6px), `fw={600}`, almost always `variant="light"`.

A badge names a state in a word. The colour comes from the status tokens, never Mantine's stock palette: map a status through `statusColor()` (status.tsx) so "Completed" is the same `ok` green on a badge, a cover and a table row, and follows the light theme.

| Status | Token |
| --- | --- |
| Ongoing, in progress | `info` |
| Completed, success | `ok` |
| Hiatus, partial | `warn` |
| Cancelled, failed | `danger` |
| Read, watched | `watched` |
| Suggestive rating, 65 to 79 score | `suggestive` |
| Unknown | `neutral` |

**The consumer provides:** the word (translated) and a status. **In a dense table use `StatusDot` instead:** a filled badge on every row turns the column into a stripe.
