---
title: Chapter upgrades and quality
description: How Maki replaces a chapter with a better copy, how quality profiles and quality formats decide what counts as better, and where the replaced file goes.
---

An upgrade swaps a chapter you already have for a better copy of the same chapter. Edit profiles in
**Settings, Downloads, Quality profiles**, formats in **Settings, Downloads, Quality formats**, and
the daily scan in **Settings, Downloads, Upgrade scan**.

## Source tiers

Every copy has a tier showing where it came from. Tiers are compared before anything else. The
default order, highest first:

1. **Volume.** A digital volume release, usually from a torrent.
2. **Official.** An official source such as MANGA Plus or WEBTOON.
3. **Scanlator.** A scanlation source, or a torrent whose tags name a group.
4. **Aggregator.** A copy scraped from an aggregator source.
5. **Unknown.** An imported file Maki has not measured, or whose origin is lost.

A profile can reorder the tiers, group two so they rank equal, and switch any tier off.

## Quality profiles

A profile decides which tiers a series accepts and when a chapter stops being upgraded. Each series
uses the default profile unless you pin another from its **Quality profile** menu.

| Setting | What it does |
| --- | --- |
| **Tier order**, **Allowed** | Which tiers count, and in what order |
| **Cutoff** | Upgrading stops once a file reaches this tier |
| **Upgrade until score** | A score a file must reach before upgrading stops. `0` ignores the score, so only the cutoff matters |
| **Minimum score gain** | How much better a same-tier copy must score to replace the file |
| **Maximum score loss for a higher tier** | How much worse a higher-tier copy may score and still replace the file. Empty means a higher tier always wins |
| **Page tolerance %** | How many fewer pages a candidate may have than the file it replaces |
| **Resolution weight**, **Compression weight** | Points for image sharpness and compression. `0` turns each off |
| **Allow replacing unknown-tier files** | Lets unmeasured imports be replaced |
| **Format scores** | Points for each quality format |
| **Let the daily scan upgrade this profile's series** | Includes the profile in the daily scan |

Starter profiles are added on first run: **Never upgrades** (keeps what was downloaded first),
**Replace aggregator copies**, **Upgrade to official releases** and **Upgrade to digital volumes**.
You can edit or delete them.

## Quality formats and scores

A quality format is a named set of conditions on a copy. Conditions can check the source, the source
kind, the image format, the release name (regex), the language, or the group. A format matches when
every required condition matches and, if it has any optional conditions, at least one of those does
too. A format with no conditions never matches.

A profile gives each format a score, and a format scores 0 unless the profile says otherwise. The
seed data's example gives `-100` to raw or machine-translated copies and `20` to a trusted digital
ripper.

A copy's score is the sum of its matched format scores plus measured points:

- **Resolution** uses the median page width. Each doubling adds the resolution weight.
- **Compression** uses image data per pixel. Each doubling adds the compression weight.

Maki measures once it has the archive. Before a download, it scores a listing from what it knows and
gives unknown attributes their best possible value, so anything that could still reach the upgrade
gets downloaded to be measured.

## When a copy replaces a chapter

A candidate replaces a file only when all of these hold:

- Its tier is allowed by the profile.
- It has at least as many pages as the file, within the page tolerance.
- The file has not already met the cutoff and the upgrade-until score.
- A higher-tier copy is not more than the maximum score loss below the file's score. A same-tier
  copy beats the file by the minimum score gain. Maki always requires at least one point, so two
  copies with the same score never swap.
- The file is not marked **Protect from upgrades**.
- The file is not shared. A volume file that backs several chapters is not replaced by one chapter's
  copy.

When a download finishes, Maki measures it and judges it again against the file as it is at that
moment, under the profile as it is then. A loss deletes the new archive. A win moves the old file
into the series' trash and puts the new one at the same path. Maki records a history entry, so the
change can be reverted.

## The daily scan

The scan runs once a day at **Daily scan hour**, in server local time, and queues upgrades for
series whose profile allows it.

- **Max upgrades per day**: `0` means no cap.
- **Quiet period (days)**: a chapter is skipped this long after it was added or last upgraded.
- **Scan incognito series**: off skips series set to No scrobble or Full incognito.

Replaced files wait in `.maki-trash/<SeriesId>/` in the root folder for **Trash retention (days)**
(default 14), then housekeeping deletes them. See
[Retention and deleting files](/Maki/guides/library/retention/).

:::tip
Converting a file to WebP or AVIF does not make it look like a worse copy. Maki scores it as the copy
it was downloaded as. See [WebP and AVIF conversion](/Maki/guides/library/page-conversion/).
:::
