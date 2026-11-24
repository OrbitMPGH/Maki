---
title: Discover
description: Recommendations, rails and metadata, all computed on your server.
---

## Recommendations

![Top pick on Discover: Demon Slayer: Kimetsu no Yaiba - Kyojuro Rengoku's Story as a spin-off, with Completed, Safe and Manga pills, a score with a rating word, Add to library and More like this, beside an Also for you list](../../../assets/ui/discover/hero.webp)
<hr>

Local ONNX embeddings over the MangaBaka dump find titles similar to your library. Seed them from
your whole library or from specific titles, and filter by year, rating, type, status, genre,
chapter count and content rating. The minimum rating moves in steps that match how scores spread,
and each step says how much of the popular catalogue it keeps. A dial moves the picks from popular
to obscure.

## Discover

![Your taste tab with What shapes your recommendations: tag bars, shelf, read and thumbs counts, tags you seem to avoid and a Recent feedback list with Undo](../../../assets/ui/discover/taste-top.webp)
<hr>

![How you read on the Your taste tab, with finish rate, typical chapter time, where you usually stop and your biggest day, lists of series you slow down for, tear through and put down, and the start of Recurring themes](../../../assets/ui/discover/taste-how-you-read.webp)
<hr>

Discover has four tabs:

- **Browse** has catalogue and genre rails, recent-reading picks, side interests and **Readers like
  you**.
- **Recommended** holds the seeded picks described above.
- **Your taste** shows reading behaviour, grouped interests, the tags steering your picks and how
  your taste has moved. Each group has **More like this**, which opens Recommended seeded from it.
- **Shortlist** holds titles you saved for later.

![Another side of your library: Iyashikei rail drawn from The Fragrant Flower Blooms with Dignity and Yotsuba&!, with Fans of and Feels like reasons under each card](../../../assets/ui/discover/side-interests.webp)

Every card shows its rating top left, and titles already in your library carry a check mark top
right. Every rail has a fullscreen **Show more** view.

![Show more view of Based on your recent activity with Genres and tags, Series and Length, year and rating filter groups, Saved filters, Hidden everywhere, a Compact, Default and Comfortable size switch and a grid of titles](../../../assets/ui/discover/show-more.webp)

A detail card opens with a compact header: the reason the card gave (such as "Feels like ...")
above the title, a line with the year, chapters and volumes, and status, content rating and type as
pills. The score says where it stands, such as "8.6 Exceptional" and "Top 1% of popular manhwa"
(8.5 and up is Exceptional, 8.0 Great and 7.0 Good). Each site is one chip with its score. Adding is
one card: **Folder** and **Incognito** show their current setting, **Automation** holds monitoring
and the quality profile, and **Preview first chapter** sits under **Add to library**. The pick at
the top of Discover uses the same header.

![Detail card for Kyojuro Rengoku's Story with the spin-off reason above the title, status, content rating and type pills, a score with site chips, an add card with Folder, Incognito, Automation, Add to library and Preview first chapter, and Synopsis, Tags, Reviews and At a glance panels below](../../../assets/ui/discover/detail-modal.webp)

Detail cards also show categorized tags, alternate titles, creators and reviews from AniList and
MyAnimeList, with the most liked Kitsu one-line takes below them. A site that does not answer is
named instead of hiding its reviews. Cards show the publication status as
a disc beside the year and chapter count. Detail cards also have thumbs up and thumbs down, and
after you mark a title seen, a toast offers **Liked it** and **Disliked it**. See
[Tune what you see](/Maki/guides/recommendations/feedback/).

## Shortlist

Save titles you might read later without adding them to the library. The series modal has a save
button, saved cards carry a ribbon, and the **Shortlist** tab lists them. Each entry can hold a note.

- Rows show what changed since you saved a title (completed, resumed, on hiatus, cancelled) and
  where you saved it from.
- Adding a title to the library takes it off the shortlist. Hiding it or giving it a thumbs down
  removes it too.
- Saving doesn't count as a taste signal for recommendations.
- Tracker [import lists](/Maki/guides/trackers/connect/#importing-your-lists) put **Plan to read**
  entries here.

![Shortlist tab with The Apothecary Diaries under Changed since you saved them, marked On hiatus since you saved it, and Delicious in Dungeon and March Comes In Like a Lion under Waiting for you, each with Preview first chapter and Add to library](../../../assets/ui/discover/shortlist.webp)

## Related works

When the catalogue lists a new sequel, prequel, spin-off or side story of a series you read, you
get a notification that opens it in Discover. Turn it off under Discover in your notification
settings. Series already in your library before the update, and series you add later, don't
announce the related works they already had.

## Metadata

One MangaBaka search identifies a series and brings along its MyAnimeList, AniList, MangaUpdates
and Kitsu IDs.

The local [MangaBaka dump](https://mangabaka.org/data/database) (nightly snapshot, about a 350 MB
download and 3.5 GB on disk) answers every metadata lookup, so search and library imports are
instant. A series MangaBaka added after the latest snapshot shows up with the next one.
MangaBaka-original data is [CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/).
