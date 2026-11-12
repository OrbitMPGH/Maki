---
type: improvement
section: Downloads
---
Stalled torrents are now caught by a limit you set under Settings > Downloads > Download client (one hour unless changed, 0 turns it off). A torrent with no progress, or with no seeds or peers connected, for that long fails with a reason that says which, leaves the client, is declined so the automatic search never grabs it again, and the search looks for the next best release. A magnet that never finds anyone to fetch its metadata from counts too, and a restart no longer starts the clock over. The new Declined releases list shows every release the search skips and lets you allow one again.
