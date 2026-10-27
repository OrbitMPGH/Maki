---
title: The local MangaBaka database and embeddings
description: What the MangaBaka snapshot and the embedding index are, how big they are, what each one powers, and how to switch them.
---

Maki keeps two local datasets so that search, metadata and recommendations work without calling an outside service for each request. The first is a copy of the [MangaBaka](https://mangabaka.org) database. The second is an index of vectors built from it, which powers semantic search and recommendations. This page covers both. For how the recommendations use them, see [How recommendations work](/Maki/guides/recommendations/how-it-works/).

Nothing about your reading is sent to MangaBaka. Both datasets are built from public catalogue data.

## The MangaBaka database

The snapshot is about 350 MB to download and 3.5 GB on disk. Maki downloads it in the background on first start and refreshes it nightly. Your setup finishes once it is installed.

Check it in **Settings → Library → Metadata database**. That card shows the snapshot size and when it was last refreshed. **Refresh now** starts a download if a newer snapshot exists. If a download fails, Maki tries again on the next scheduled run.

The snapshot powers:

- Metadata lookups. A search identifies a series and brings its MyAnimeList, AniList, MangaUpdates and Kitsu IDs, and library imports resolve without a network call.
- The catalogue rails on Discover, such as **Trending now**, **Browse the catalogue** and **Every genre**. See [Discover rails](/Maki/guides/recommendations/rails/).
- Custom rails that draw from the catalogue or from recommendations.

A series MangaBaka added after the latest snapshot appears with the next one.

## Semantic search and recommendations

The embedding index is a vector for each series, built with a local ONNX model. Two series about similar things end up close together, even when they share no tags. Search uses it to match by meaning, and the recommendations use it to find similar titles.

Turn it on or off in **Settings → Library → Semantic search & recommendations**. The switch has two states: off, and on with the base model. It needs the MangaBaka database first.

Maki tries to download the prebuilt vectors first. If none are available, it builds the index on your own machine, which takes a long time on a CPU. When the index is on, it uses about 240 MB of RAM.

With the switch off:

- Search falls back to title matching.
- Rails that need the index are left out. Side interests, for example, need it to have any rows.

Turning it on starts a model switch that downloads the model and the index. The status line shows progress until it finishes.

## Memory settings

These environment variables control how long Maki keeps each dataset in memory after it was last used. Set them in the container or service environment, then restart Maki.

| Variable | Default | What it controls |
| --- | --- | --- |
| `MAKI_EMBED_IDLE_MINUTES` | 5 | Minutes before the embedding model is unloaded. `0` keeps it loaded. |
| `MAKI_VECTOR_IDLE_MINUTES` | 15 | Minutes before the search vectors are unloaded. |
| `MAKI_ARTIFACT_IDLE_MINUTES` | 10 | Minutes before the catalogue, co-read, graph and cohort data are unloaded. `0` turns unloading off. |

Unloading saves memory. The cost is a short reload on the next search or recommendation after an idle period. The embedding model reloads in a second or two. Leave the defaults unless memory is tight or you search often.

## Licence

MangaBaka-original data is [CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/), which is non-commercial and share-alike. Check that licence before you reuse the data outside Maki.
