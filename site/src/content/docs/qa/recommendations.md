---
title: Recommendations
description: Why Discover is empty, what makes suggestions appear, and how to hide titles.
---

### Why is Discover empty?

Discover needs the local MangaBaka database (about 350 MB to download, 3.5 GB on disk). The tab stays hidden until it is available, and catalogue rails show "Nothing to browse yet" while it downloads. Check progress under **Settings → Library → Metadata database**.

### Why does Discover say "Nothing to recommend yet"?

Recommendations start from series you already have. Add some through **Add series**. [How recommendations work](/Maki/guides/recommendations/how-it-works/) explains the seeds and rails.

### How do I stop a title from showing up?

Give it a thumbs down. That hides the title and pushes down titles that sit closer to it than to the ones you kept. To remove only that title without affecting anything else, use **Hide this title** in its menu instead. See [Tune what you see](/Maki/guides/recommendations/feedback/).

### How often do the recommendations update?

The MangaBaka database is a nightly snapshot. A series that MangaBaka adds after the latest snapshot
shows up with the next one. Everything else is computed on your server from that local copy. See
[Discover](/Maki/features/discover/).
