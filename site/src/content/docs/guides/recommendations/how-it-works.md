---
title: How recommendations work
description: What the Discover engine does, and what stays on your server.
---

Recommendations are computed on your server. Your reading is sent only to the trackers you connect.
Anime lookups go to AniList and MyAnimeList, and detail cards fetch MyAnimeList reviews.

## The index

Maki downloads a local copy of the [MangaBaka](https://mangabaka.org) database (about 350 MB, 3.5 GB
on disk) and builds a vector for each series with a local ONNX embedding model. Series about similar
things end up close together, which finds matches that shared genre tags would miss. See
[The local MangaBaka database](/Maki/guides/recommendations/local-database/).

## Seeds

Recommendations start from titles you already have. Seed them from your whole library or from
specific titles, then filter by year, rating, type, status, genre, chapter count, content rating and
obscurity, from popular to hidden gems.

## Rails

The Browse tab is a stack of rails. Reorder or switch off any of them per user in the layout editor,
and open any rail fullscreen with **Show more**. See
[Discover rails](/Maki/guides/recommendations/rails/) for each one.

- **Genre and catalogue rails** browse the MangaBaka catalogue.
- **Readers like you** looks at what people with similar reading go on to read.
- **Your taste** shows your reading behaviour, grouped interests and how your taste has moved.

Detail cards show categorized tags, alternate titles, creators, per-source ratings and MyAnimeList
reviews. The Discover page itself is described in [Discover](/Maki/features/discover/).
