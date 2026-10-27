---
title: What is Maki
description: A self-hosted manga manager in the spirit of Sonarr and Radarr, with a reader built in.
---

Maki watches manga sites for new chapters, downloads the pages and writes CBZ files with
ComicInfo.xml metadata. [Kavita](https://www.kavitareader.com) and most comic readers understand
that format, so the same folder works everywhere. You can also read in Maki's own reader, over
OPDS in Panels, Chunky, KOReader and Mihon, or in apps that speak Komga's API.

:::note
Maki is almost entirely AI-built, developed with Anthropic's latest Claude models. It is pre-1.0,
so the schema and API can still change between releases. Issues and pull requests are welcome.
:::

## Why Maki

- **Recommendations from your own library.** Local embeddings over the [MangaBaka](https://mangabaka.org)
  catalog suggest what to read next, all on your server. See
  [How recommendations work](/Maki/guides/recommendations/how-it-works/).
- **Automatic monitoring.** Maki picks the best source for each series and queues new chapters as
  they appear. An Upcoming view shows when the next chapter is likely.
- **Scrobbling.** Reading progress goes to AniList, MyAnimeList, Kitsu, MangaBaka and MangaUpdates,
  whether you read in Maki, over OPDS or in Kavita.
- **Plain files, any reader.** Everything is saved as CBZ with ComicInfo.xml, and nothing is locked
  inside Maki's database.
- **A reader when you want one.** Paged, double-page or continuous scroll, with progress merged
  across every app you read in.

## Where to go next

- New here? Follow [Install with Docker Compose](/Maki/installation/docker-compose/), then
  [Add your first series](/Maki/guides/getting-started/add-your-first-series/).
- Running Unraid? See [Unraid](/Maki/installation/unraid/).
- Want to use your favourite reading app? See [Reading over OPDS and Komga](/Maki/guides/reading/opds-apps/).

## Legal

You are responsible for the sources you connect and for the laws where you live. Read
[Legal and responsible use](/Maki/introduction/legal/) before you add a source.
