---
title: The local MangaBaka database and embeddings
description: What the MangaBaka snapshot and the embedding index are, how big they are, what each one powers, and how to switch them.
---

Maki keeps two local datasets so search, metadata and recommendations work without calling an outside
service per request: a copy of the [MangaBaka](https://mangabaka.org) database, and an index of
vectors built from it. Both are built from public catalogue data, and nothing about your reading is
sent to MangaBaka unless you connect it as a tracker. For how recommendations use them, see
[How recommendations work](/Maki/guides/recommendations/how-it-works/).

## The MangaBaka database

The snapshot is about 350 MB to download and 3.5 GB on disk. Maki downloads it in the background on
first start, then checks every six hours and installs each new snapshot once MangaBaka publishes
one. Your setup finishes once it is installed.

**Settings → Library → Metadata database** shows its size and last refresh. **Refresh now** downloads
a newer snapshot if one exists. A failed download is retried after 15 minutes, then 30, 60 and so on, while no usable snapshot is
installed. Otherwise it waits for the next six-hourly run. A series
MangaBaka added after the latest snapshot appears with the next one.

The snapshot powers:

- Metadata lookups. A search identifies a series and brings its MyAnimeList, AniList, MangaUpdates
  and Kitsu IDs, and library imports resolve without a network call.
- Catalogue rails on Discover, such as **Trending now**, **Browse the catalogue** and **Every
  genre**. See [Discover rails](/Maki/guides/recommendations/rails/).
- Custom rails that draw from the catalogue or from recommendations.

## Semantic search and recommendations

The embedding index is a vector per series, built with a local ONNX model. Search uses it to match by
meaning, and recommendations use it to find similar titles, even when they share no tags.

Toggle it in **Settings → Library → Semantic search & recommendations**. It needs the MangaBaka
database first. Turning it on starts a model switch that downloads the model and index, with progress
on the status line. Maki tries the prebuilt vectors first. If none are available, it builds the index
on your machine, which takes a long time on a CPU. While loaded, it uses about 240 MB of RAM. It unloads after 5 idle minutes by default.

With the switch off, search falls back to title matching, and rails that need the index are left out.
Side interests, for example, have no rows without it.

## Memory settings

Set these environment variables in the container or service environment, then restart Maki. They
control how long each dataset stays in memory after last use.

| Variable | Default | What it controls |
| --- | --- | --- |
| `MAKI_EMBED_IDLE_MINUTES` | 5 | Minutes before the embedding model is unloaded. `0` keeps it loaded. |
| `MAKI_VECTOR_IDLE_MINUTES` | 15 | Minutes before the search vectors are unloaded. |
| `MAKI_ARTIFACT_IDLE_MINUTES` | 10 | Minutes before the catalogue, co-read, graph and cohort data are unloaded. `0` turns unloading off. |

Unloading saves memory at the cost of a short reload (a second or two for the embedding model) on the
next search or recommendation. Leave the defaults unless memory is tight or you search often.

## Licence

MangaBaka-original data is [CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/),
which is non-commercial and share-alike. Check it before you reuse the data outside Maki.
