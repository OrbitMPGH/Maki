---
type: improvement
section: Performance
---
An idle Maki does much less background work, so NAS disks can spin down: fewer write checks on root folders, a daily KOReader sweep, a page conversion worker that sleeps until it has work, no queue polling in open tabs while the live connection works, and MangaFire syncs that stop at the first page of known chapters.
