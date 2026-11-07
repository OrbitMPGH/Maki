---
type: improvement
section: Downloads
issue: 170
by: avalynnrose
---
Smarter source order for downloads:

- A source that keeps failing (three failures for every success over 30 days) moves behind the others in the automatic order. A manual order is left alone.
- Sources measured on the series go ahead of unmeasured ones, and paused or cooling-down sources are tried last.
- A Suwayomi source that wants a login or purchase for a chapter fails over to the next source straight away, and purchase failures are no longer retried every five minutes. A manual retry still works once you've logged in or bought it.
- Sources that only serve previews are detected and tried last.
