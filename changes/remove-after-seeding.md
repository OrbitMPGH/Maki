---
type: feature
section: Downloads
---
Maki can now remove a torrent from the download client after it has seeded, instead of right after import. Pick "After seeding" under Settings > Downloads > Download client and set a seed ratio, a seeding time or both; the torrent goes once it reaches either. When you add a download client Maki reads its own default seed limits (qBittorrent's ratio and seeding time, the ratio limit from Transmission and Deluge), and a button reads them again later. Maki checks the targets itself, so a torrent you paused by hand is not mistaken for one that finished seeding.
