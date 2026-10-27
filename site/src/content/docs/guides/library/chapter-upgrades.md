---
title: Chapter upgrades and quality
description: How Maki replaces a chapter with a better copy, how quality profiles and quality formats decide what counts as better, and where the replaced file goes.
---

An upgrade swaps a chapter you already have for a better copy of the same chapter. This page covers
how Maki decides a copy is better, and what happens to the file it replaces. Profiles are edited in
**Settings, Downloads, Quality profiles**. Formats are edited in **Settings, Downloads, Quality
formats**. The daily scan is **Settings, Downloads, Upgrade scan**.

## Source tiers

Every copy has a tier, which says where it came from. Tiers are compared before anything else. From
highest to lowest, the default order is:

1. **Volume.** A digital volume release, usually from a torrent.
2. **Official.** A copy from an official source such as MANGA Plus or WEBTOON.
3. **Scanlator.** A scanlation source, or a torrent whose tags name a group.
4. **Aggregator.** A copy scraped from an aggregator source.
5. **Unknown.** An imported file that Maki has not measured, or whose origin is lost.

A profile can reorder the tiers, group two of them so they rank equal, and switch any tier off.

## Quality profiles

A profile decides which tiers a series accepts and when a chapter stops being upgraded. Each series
uses the default profile unless you pin another one from its **Quality profile** menu. A profile
has these settings:

- **Tier order** and **Allowed**: which tiers count, and in what order.
- **Cutoff**: upgrading stops once a file reaches this tier.
- **Upgrade until score**: a score a file must reach before upgrading stops. `0` means the score
  plays no part and only the cutoff matters.
- **Minimum score gain**: how much better a copy in the same tier must score to replace the file.
- **Maximum score loss for a higher tier**: how much worse a copy from a higher tier may score and
  still replace the file. Leave it empty to let a higher tier always win.
- **Page tolerance %**: how many fewer pages a candidate may have than the file it replaces.
- **Resolution weight** and **Compression weight**: points for image sharpness and compression.
  `0` turns each one off.
- **Allow replacing unknown-tier files**.
- **Format scores**: points for each quality format.
- **Let the daily scan upgrade this profile's series**.

Starter profiles are added when Maki first runs. They include **Never upgrades**, which keeps what
was downloaded first, **Replace aggregator copies**, **Upgrade to official releases**, and **Upgrade
to digital volumes**. You can edit or delete them.

## Quality formats and scores

A quality format is a named set of conditions on a copy. Conditions can check the source, the source
kind, the image format, the release name against a regex, the language, or the group. A format
matches when every required condition matches and, if the format has any optional conditions, at
least one of them does too. A format with no conditions never matches.

A profile gives each format a score. A format scores 0 unless the profile says otherwise, so the
score only counts when you set it. The example in the seed data gives `-100` to raw or machine
translated copies and `20` to a trusted digital ripper.

The score of a copy is the sum of its matched format scores, plus measured points:

- **Resolution** uses the median page width. Each doubling of width adds the resolution weight.
- **Compression** uses image data per pixel. Each doubling adds the compression weight.

Measurement happens once Maki has the archive. Before a download, Maki scores a listing from what it
knows and gives unknown attributes their best possible value. Anything that could still reach the
upgrade gets downloaded to be measured.

## When a copy replaces a chapter

A candidate replaces a file only when all of these hold:

- Its tier is allowed by the profile.
- It has at least as many pages as the file, within the page tolerance.
- The file has not already met the cutoff and the upgrade-until score.
- A copy from a higher tier is not more than the maximum score loss below the file's score. A copy
  from the same tier must beat the file by the minimum score gain. Maki always requires at least one
  point, so two copies with the same score never swap places.
- The file is not protected. A file marked **Protect from upgrades** is never replaced.
- The file is not shared. A volume file that backs several chapters is not replaced by one chapter's
  copy.

When a download finishes, Maki measures it and judges it again against the file as it is at that
moment, under the profile as it is then. A loss deletes the new archive and leaves your library
alone. A win moves the old file into the trash for the series and puts the new one at the same
path. Maki also records a history entry for the change, so it can be reverted.

## The daily scan

The scan runs once a day at the hour set by **Daily scan hour**, in server local time. It queues
upgrades for series whose profile allows the scan. Limits on the scan:

- **Max upgrades per day**. `0` means no cap.
- **Quiet period (days)**. A chapter is skipped for this long after it was added or last upgraded.
- **Scan incognito series**. Off skips series set to No scrobble or Full incognito.

Replaced files wait in `.maki-trash/<SeriesId>/` in the root folder for **Trash retention (days)**,
which defaults to 14. After that, housekeeping deletes them.

:::tip
Converting a file to WebP or AVIF does not make it look like a worse copy. Maki scores it as the
copy it was downloaded as. See [WebP and AVIF conversion](/Maki/guides/library/page-conversion/).
:::
