---
title: Recommendations
description: Why Discover is empty, what makes suggestions appear, and how to hide titles.
---

### Why is Discover empty?

Discover needs the local MangaBaka database, so the tab stays hidden until it is available. The
database is about 350 MB to download and 3.5 GB on disk. While it is still downloading, the catalogue
rails show "Nothing to browse yet". Check its progress under **Settings → Library → Metadata database**.

### Why does Discover say "Nothing to recommend yet"?

Recommendations start from series you already have. Add some series to your library, through
**Add series**, and Maki suggests more like them. The
[How recommendations work](/Maki/guides/recommendations/how-it-works/) guide explains the seeds and the rails.

### How do I stop a title from showing up?

Give it a thumbs down. That hides the title and pushes down titles that sit closer to it than to the
ones you kept. Hiding a title only removes it from view, and does not change how your other suggestions
are ranked.

### How often do the recommendations update?

The MangaBaka database is a nightly snapshot. A series that MangaBaka adds after the latest snapshot
shows up with the next one. Everything else is computed on your server from that local copy. See
[Discover](/Maki/features/discover/).
