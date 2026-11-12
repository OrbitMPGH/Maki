---
type: feature
section: Downloads
issue: 178
by: avalynnrose
---
New opt-in setting under Downloads: "Look for volume releases first". When you add a series with nothing on disk, Maki asks your indexers for a volume release as soon as its sources are matched, before downloading anything chapter by chapter. That search also takes a release that only adds chapters. It follows the series' quality profile: a series whose profile doesn't upgrade to volumes downloads from its sources as before.

- While that search runs, while a volume release waits for your answer (up to 48 hours) and while one downloads (up to 72 hours), automatic downloads from sources (monitoring and Smart Download) skip the chapters the release covers. Chapters past its last volume download as usual, and the skipped ones are queued once the wait ends if the release did not bring them.
- Series from an import list are searched a few at a time by the volume search job, so a long list does not flood your indexers. Their source downloads wait up to a day for it.
- Series you already have are not affected, and neither is the daily volume search. Searching by hand with the setting on also judges releases that only add chapters.
- Downloads you start yourself and request approvals are never held. The series page says what is waiting and has a "Download from sources now" button.
- Downloading every wanted chapter by hand now mentions a pending volume release and lets you grab it instead.
