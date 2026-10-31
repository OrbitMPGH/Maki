---
title: Ranking and choosing sources
description: How Maki matches a series to sources, which one it downloads from first, and how to link, unlink, enable and rank them.
---

For the sources themselves, see [Built-in sources](/Maki/guides/sources/built-in-sources/).

## Automatic matching

When you add a series, Maki searches every enabled source for it in the background, and the series
shows as matching until the searches finish.

- A result is accepted when its title agrees closely enough with the series. If the main title finds
  nothing on a source, Maki retries with alternative titles: up to two English ones on a source that
  publishes English, otherwise the alt title in the source's own language, then the original title if
  its script fits.
- Some sources publish tracker ids such as MyAnimeList or AniList. When one matches the series, Maki
  accepts the result without comparing titles.
- WEBTOON CANVAS results are user-made, so they only match when the credited author agrees with the
  series' author. A series with no author on record never matches CANVAS automatically, but you can
  link it by hand.

Each linked source shows how it got there: **Title search**, **Cross-id** or **Manual**. A failed
search is reported as a failed source, not "no match", so a broken site does not look like a missing
series.

**Auto-match** in the Sources section of the series page searches again. It only tries sources that
are not linked yet.

## Priority order

In **Settings → Downloads → Sources → Manage sources**, drag sources into order in the **Priority**
panel beside **All sources**. On narrow screens it is a tab instead. The top source is tried first
when several carry the same series. Newly enabled sources join at the bottom.

Languages affect the order too. In **Settings → Downloads → Languages**, list your languages, most
preferred first. Maki ranks each source by the most preferred enabled language it publishes in, and
uses the priority order to break ties inside that group. A source that publishes none of your
enabled languages is skipped by auto-match, but you can still link it by hand.

## Download order

Two settings decide how a source is picked for each download.

- **Settings → Downloads → Sources → Pick a source by** sets the default. It is either **Your
  priority order** or **Best measured quality**.
- The **Download order** menu on a series overrides that default for one series. Its options are
  the default, **Manual priority** and **Best quality first**.

Each linked source also has a priority number. In manual order, a lower number is tried first. In
quality order, the priority number only breaks ties between sources of equal quality.

Quality comes from sampling. **Measure** on the series page takes a few pages from three chapters on
every enabled source and records a quality tier for each, and **Compare** shows two sources side by
side. To measure new series automatically, turn on **Measure sources when a series is added** in the
Sources section of Downloads settings.

## Linking a source

On the series page, select **Link source** in the Sources section, pick a source, then search for
the series or paste its URL. A source with no search only works with a pasted URL.

## Unlinking a source

Select **Remove mapping** on the source's row and confirm in the **Remove source?** dialog. Chapters
that no other enabled source lists are removed. Files downloaded from this source are detached, so
they can't be read as the chapter they used to be. Reading progress and bookmarks for the removed
chapters are deleted too.

- **Remove and clean up** does the removal above. Tick **Also delete detached files from disk** to
  delete those files as well, if your account is allowed to delete series.
- **Remove without cleanup** skips the chapter clean-up. You confirm it in a second dialog.

## Turning sources off

- In **Manage sources**, the switch on a source's row turns it off for the whole library. Its links
  stay in place and come back when you turn it on again. Each series that uses it shows a note that
  the series' own setting is kept.
- The **Enabled** switch on a series' source row turns that one link off for that series.

:::tip
Suwayomi sources are all off until you turn them on. A mixed-language Suwayomi source also needs a
chapter language picked before it can be used. See [Suwayomi extensions](/Maki/guides/sources/suwayomi/).
:::
