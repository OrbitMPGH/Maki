---
type: fix
section: Downloads
---
Fixed the volume search grabbing torrents nobody seeds, and leaving a dead torrent downloading forever. Releases with no seeders are skipped, a release that overlaps a torrent still downloading for the series is not grabbed, and a torrent that makes no progress for an hour while the client is downloading it is marked failed, taken out of the client when Maki added it, and not picked again.
