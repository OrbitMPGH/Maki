---
title: KOReader
description: OPDS downloads and progress sync with KOReader.
---

KOReader can talk to Maki two ways: as an **OPDS catalogue** to browse and download chapters,
and as a **progress sync server**, so chapters you read on the device, online or offline, show
up as read in Maki and you can carry on in Maki's reader where you stopped.

Both use your OPDS token. Turn on the catalogue under **Settings → Reading → OPDS catalogue**
first and keep the feed URL it shows you; the token is the part after `/api/v1/opds/`.

## Download chapters over OPDS

In KOReader, open the file browser's **OPDS catalog**, add a catalogue and paste the feed URL.
From outside your network, swap the host for the address you use there.

## Sync progress

1. Open a book in KOReader and go to **Tools → Progress sync → Custom sync server**. Enter your
   Maki address followed by `/api/v1/kosync`, for example `http://192.168.1.20:8990/api/v1/kosync`.
   **Settings → Reading → KOReader progress sync** shows the address to copy.
2. Choose **Register / Login**. Enter your **OPDS token** as the username. Type anything as the
   password; Maki doesn't use it.
3. Set **Document matching method** to **Binary**.

**Push progress** and **Pull progress** in the same menu sync by hand. With **Automatically keep
documents in sync** on, KOReader pushes your page as you read and when you close the book or the
device sleeps, and asks Maki for the latest position when a book opens.

What happens with a pushed page:

- It goes through the same progress path as Maki's own reader, so it merges with everything else,
  counts toward Rewind and achievements, and reaches AniList, MyAnimeList and the other trackers.
  Fully incognito series record the position and nothing else, as they do in the reader.
- A volume archive holds several chapters. A page inside it marks the chapters before it read and
  saves your place in the chapter it falls in. When Maki can't tell where chapters start (the
  archive has no chapter markers in its page names), the chapters in it are only marked read once
  you reach the last page.
- Books that didn't come from Maki are accepted and ignored, so they don't hold up KOReader's
  retry queue.
- A page pushed for a file Maki hasn't recognised yet, such as one you copied over before the
  background pass got to it, is kept for up to a week and applied once the pass has recorded the
  file. It is only kept in memory, so restarting Maki drops it; push again from KOReader afterwards.

## How a file is recognised

KOReader identifies a book by a document id. In **Binary** mode it is an MD5 of a few small
samples of the file's bytes, so the copy on the device has to be byte-for-byte a file from your
library. Maki records the id when the OPDS catalogue serves a download, and a background pass
records it for every file in the library every six hours, so files you copy over USB or a sync
tool match too. An unknown id makes that pass run sooner, so a file added a moment ago matches on
the next sync. When Maki rewrites a file (a ComicInfo update, a rename that touches the archive),
the ids of the old bytes keep working for copies already on a device.

**Filename** mode is an MD5 of the file name. Maki matches the name a file has in your library and
the name the OPDS download was served under, but KOReader names OPDS downloads after the catalogue
entry unless **Use server filenames** is on, and those names are often the same across series
("Ch. 12"). A name that fits more than one file matches nothing. Use Binary.

Regenerating the OPDS token signs KOReader out as well; log in again with the new token.
