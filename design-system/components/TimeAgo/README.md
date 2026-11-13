# TimeAgo

A row's timestamp, short: "3m ago" and "5h ago" for the first day, "yesterday" for the second, then a short date ("Nov 4"). The full date and time show in a tooltip after 300ms, and the element is a real `<time dateTime>` so assistive tech reads the exact value.

Use it for a stamp that stands alone in a table cell, a card or a meta line. A stamp inside a sentence uses the long relative form from `time.ts` instead ("3 minutes ago"). It renders nothing for an empty or unparseable value, so a column does not need its own guard. Add `tnum` where timestamps line up in a column.

**The consumer provides:** `value` (an ISO string) and optionally `className`.
