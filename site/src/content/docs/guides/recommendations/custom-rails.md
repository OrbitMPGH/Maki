---
title: Custom rails
description: Build your own rails on Home and Discover from your library, the recommender or the catalogue.
---

A custom rail is a saved filter with a source. It draws one row on Home or Discover. This page covers how to create one, what each source does, and how to manage them. For the built-in rails, see [Discover rails](/Maki/guides/recommendations/rails/).

## Create a rail

There are two ways to start one.

- Press **Add a rail** at the bottom of Home or Discover.
- Set up a filter on Discover's catalogue, a creator page or the Recommended tab, open **Saved filters**, and press **Save as a rail…**. The rail starts with those filters already set.

You can have up to 30 custom rails.

## The editor

The editor is titled **New rail**, or **Edit rail** when you change one. Its fields are:

- **Name.** Shown as the rail's title. Any text works.
- **Show on.** Choose **Home** or **Discover**.
- **Titles from.** Choose a source, described below.
- **Sort by.** The sort options depend on the source.
- **Leave out series in your library.** Catalogue rails only. It is on by default.
- **Obscurity** and **Variety.** Recommendation rails only. Obscurity moves between **popular** and **gems**. Variety moves between **closest** and **varied**.

Filters are set with the same panel as the catalogue. Use **Has all of**, **Has any of** or **Has none of** for genres and tags.

Press **Add rail** to create the rail, or **Save** when editing.

## Sources

**Your library** shows series you already have that match the filters. It is only available on Home, because Discover is about what you do not own.

**Recommendations** ranks picks the same way the Recommended tab does with the same settings. You can seed it with specific series. It needs the local MangaBaka database, and its order is the ranking, so it has no sort.

**Catalogue** shows anything in the MangaBaka catalogue that matches the filters. It also needs the local MangaBaka database.

If a source needs the database and it is not ready, the rail says **This rail needs the local MangaBaka database, which is switched off or still downloading.** See [The local MangaBaka database](/Maki/guides/recommendations/local-database/).

## Sort options

- **Your library:** **Recently added**, **Recently read**, **Title**, **Most popular**
- **Catalogue:** **Most popular**, **Top rated**, **Newest**, **Oldest**

## Arrange and remove rails

Rails are placed on the page by the layout editor. Use **Edit layout** on Home or Discover to move a rail or switch it off. Rail changes are saved straight away. Press **Done** to save the layout.

Catalogue rails have a **Show more** button that opens the rail with its filters, so you can narrow it further there.

To delete a rail, open its options and press **Delete rail**. Maki asks first, and the rail's filters go with it.
