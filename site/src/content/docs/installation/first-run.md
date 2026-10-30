---
title: First-run setup
description: Create the administrator account, work through the setup guide, add a library folder, and wait for the MangaBaka database.
---

## 1. Create the administrator account

Open Maki in your browser, for example `http://localhost:8990`. A new install has no users, so the
first screen is **Create your account**. The password needs at least 10 characters, with no symbol
rules.

This account is the administrator. Add more people later from the users section of **Settings**.

The screen also has a short checklist for public installs: HTTPS, the trusted proxy setting and
two-factor sign-in. See [Reverse proxy and HTTPS](/Maki/installation/reverse-proxy-https/) before
you expose Maki outside your network.

## 2. Work through the setup guide

The setup guide opens after the account is created. It has six steps:

1. **Welcome.** Pick the interface language. Appearance is saved on this device only.
2. **Library.** Add your library folders, then choose what Maki does with folders it finds: rename
   them to Maki's standard, or keep their names and download into them or into a new folder. Two
   more switches write **ComicInfo.xml** into imported files and save a `cover.jpg` into each series
   folder.
3. **Discover.** Shows the status of the local MangaBaka database. Also sets **Recommendations and
   search by description** and the most explicit content rating Discover may show outside your own
   library.
4. **Downloads.** Pick the languages Maki looks for and which chapters it skips.
5. **Connections.** Optional. Add tools you already run, such as FlareSolverr, Kavita, an indexer or a
   download client. Maki downloads from its built-in sources without any of them.
6. **Finish.** A summary, and links to add a series, import what you have, connect trackers or invite
   people.

Every change saves as you go. Each setting is also in **Settings** afterwards, and you can reopen the
guide from the top of Settings.

## 3. Add a library folder

In the **Library** step, enter the path as Maki sees it in **Folder path**. In Docker that is
`/library`, the container path from the compose file.

Maki shows the free space on each folder. If a row says **Maki can't reach this folder**, check the
mount and the permissions. See [Volumes, PUID and PGID](/Maki/installation/volumes-and-permissions/).

Removing a root folder does not touch the files on disk. It does remove every user's access to that folder.

## 4. Wait for the MangaBaka database

Series details and search come from a local copy of the [MangaBaka](https://mangabaka.org) database,
about 350 MB to download and 3.5 GB on disk. Maki downloads it in the background, and the
**Discover** step shows its progress.

Until it is installed, Maki shows **Getting the metadata database ready**. It opens by itself when
the download finishes, so you do not need to refresh.

- An administrator sees the progress, with **Open settings** and **Open health** for when a download fails.
- Other users see a waiting screen, or a message saying the download failed and is retried on its own.
- Reading apps connected over OPDS, Komga or KOReader keep working while you wait.

The setup guide does not finish until the database is installed.

## Next

[Add your first series](/Maki/guides/getting-started/add-your-first-series/).
