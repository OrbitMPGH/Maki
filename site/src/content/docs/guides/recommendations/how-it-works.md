---
title: How recommendations work
description: What the Discover engine does, and what stays on your server.
---

Recommendations run entirely on your server. Nothing about your reading is sent anywhere.

## The index

Maki downloads a local copy of the [MangaBaka](https://mangabaka.org) database (about 350 MB, 3.5 GB
on disk) and builds a vector for each series with a local ONNX embedding model. Two series that are
about similar things end up close together, which finds matches that shared genre tags would miss.

## Seeds

Recommendations start from titles you already have. Seed them from your whole library or from
specific titles, then filter by year, rating, type, status, genre, chapter count, content rating
and popularity.

## Rails

The Browse tab is a stack of rails. Every part can be reordered or switched off per user in the
layout editor, and every rail has a fullscreen **Show more** view.

- **Genre and catalogue rails** browse the MangaBaka catalogue.
- **Blind spots** show what your library has little of.
- **Readers like you** looks at what people with similar reading go on to read.
- **Your Taste** shows your reading behaviour, grouped interests and how your taste has moved.

Detail cards show categorized tags, alternate titles, creators, per-source ratings and MyAnimeList
reviews.

## Related

The full feature list is in [Features](/Maki/introduction/features/#discover).
