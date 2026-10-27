---
title: Import a Mihon backup
description: Bring your Mihon library, categories and read chapters into Maki from a Mihon backup file.
---

Only manga in your Mihon library are imported. The **Mihon backup** tab on the Import page appears
only after the local MangaBaka database is installed, because every title is matched against it.

## Make the backup

In Mihon, open **More > Settings > Data and storage > Create backup**. Maki accepts the `.tachibk`
file it makes, or a `.proto.gz`, up to 100 MB.

## Read the backup

1. Open **Import** and choose the **Mihon backup** tab.
2. Choose the file and click **Read backup**.

Maki matches each manga to MangaBaka in this order: MangaBaka id, AniList, MyAnimeList, Kitsu, and
then the title. A title match is accepted only when exactly one result scores highest. Two works
with the same title are left for you to choose.

The page shows **Matching your library to MangaBaka** while this runs, so keep it open. If it stops,
**Retry** carries on, or you can review what matched so far. Anything left over shows as not found
and can be matched by hand.

## Review

Filter the review table by **All**, **Matched**, **Needs review**, **Not found** or **In library**.
Each row shows the Mihon title, the match and what it matched by. **Find match** or **Change** picks
a different result.

Settings above the table apply to the whole import:

- **Root folder** is where the new series are stored.
- **Monitor for new chapters** and **New chapters** set the monitor mode for each series.
- **Apply categories as tags** turns each Mihon category into a tag. Manga in Mihon's Default
  category get no tag.
- **Mark chapters read in Mihon as read** marks the chapters you read in Mihon.

Series already in your library are not added again. Importing them applies tags and read state only.

If your account cannot add series, a new series is sent to an admin as a request, and tags and read
marks apply only to series already in the library. One run sends at most 10 requests.

## After the import

The **Import finished** summary lists anything skipped or failed, with the reason.

- Read ticks show in the chapter list. Series read counts only include downloaded chapters.
- Read marks from an import are not reading Maki saw happen, so they do not add to Rewind or earn XP.
- If a series has no chapters yet, its read marks wait and apply after the first sync that finds
  chapters.
- The import only marks chapters read, never unread, so anything you already read in Maki stays read.
