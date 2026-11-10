---
type: feature
section: Downloads
issue: 178
by: avalynnrose
---
New opt-in setting under Downloads: "Look for volume releases first". When a series is added and its sources are matched, Maki asks your indexers for a volume release before downloading anything chapter by chapter. A series with no files can now take a volume release, and a release that only adds chapters is judged like any other.

- While that search runs, while a volume release waits for your answer (up to 48 hours) and while one downloads (up to 72 hours), automatic downloads from sources (monitoring and Smart Download) skip the chapters the release covers. Chapters past its last volume download as usual, and the skipped ones are queued once the wait ends if the release did not bring them.
- Downloads you start yourself and request approvals are never held. The series page says what is waiting and has a "Download from sources now" button.
- Series added by a library import or an import list are searched by the daily volume search instead, so a big import does not flood your indexers.
- Downloading every wanted chapter by hand now mentions a pending volume release and lets you grab it instead.
