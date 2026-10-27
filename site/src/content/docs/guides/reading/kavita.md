---
title: Kavita alongside Maki
description: Share one library folder with Kavita, and keep scrobbling and read status in step between the two.
---

Maki writes plain CBZ files with ComicInfo.xml into your folders, so Kavita can read the same library.
Connecting Kavita is optional. It lets Maki ask Kavita to scan new files, send cover and metadata,
and keep read status in step.

## Share the library folder

Point Kavita and Maki at the same folder. In Docker that means mounting the same host folder into
both containers. See [Docker Compose](/Maki/installation/docker-compose/) and
[Volumes and permissions](/Maki/installation/volumes-and-permissions/).

If Kavita sees the folder under a different path, set the path mapping in **Settings → Integrations →
Kavita**:

- **Path as Maki sees it** is the folder as Maki sees the library. Only needed when Kavita sees the
  library under a different path, for example in Docker.
- **Path as Kavita sees it** is the same folder as Kavita sees it, for example `/manga`.

Add the URL and API key in the same card. The API key is under **User Settings → 3rd Party Clients**
in Kavita.

## What Maki does on its own

Once Kavita is connected, Maki does the following without any switch to turn on:

- After a download, import or move, it asks Kavita to scan that series folder. Kavita runs the scan
  about a minute later, so the new chapters appear shortly after.
- Once Kavita has created the series, Maki sends the MangaBaka poster as its cover, the web links, and
  the publication status.
- Reading in Kavita is scrobbled to your trackers. Maki checks every 30 minutes by default. The
  **Sync now** button on the Scrobble page checks straight away. See also
  [Trackers](/Maki/features/trackers/).

Maki matches a series to its Kavita counterpart by title or folder name, ignoring punctuation. A
Kavita series with a very different name does not link to a series in Maki.

## Read status

Read status has three switches and one import. They sit under **Settings → Reading → Kavita read
sync**.

- **Mark chapters read in Kavita too** sends reads from Maki to Kavita. It only applies to series
  that match a Kavita series.
- **Mark chapters read here as soon as they're read in Kavita** pulls reads from Kavita live. Kavita
  only sends reading updates to admin accounts, so the API key must belong to a Kavita admin. Maki
  shows a message when it does not.
- **Import read status** copies read state from Kavita once. It is safe to run again, and it never
  marks anything unread. Kavita does not say when those chapters were read, so the import does not
  add them to Rewind.

## One Kavita account

Kavita is one server with one API key, so its reading belongs to one Maki account. Choose that
account under **Settings → Integrations → Kavita → Attribute Kavita's reading to**. If you leave it
empty, the lowest-numbered admin is used. Only that account imports from Kavita or pushes reads back.

For the scrobbling side, **Scrobble reading from these libraries** on the same card limits which
Kavita libraries are read. Leave it empty to read all of them.

:::tip
If the read sync switches say **Kavita's reading belongs to another Maki account**, the account
chosen above is not yours. Only that account can change read status with Kavita.
:::
