---
title: KOReader
description: OPDS downloads and progress sync with KOReader.
---

KOReader works with Maki as an **OPDS catalogue** (browse and download chapters) and as a
**progress sync server** (chapters you read on the device, online or offline, show as read in Maki,
and you can carry on in Maki's reader where you stopped).

Both use your OPDS token. Turn on the catalogue under **Settings → Reading → OPDS catalogue** first
and copy the feed URL it shows. It is shown only once, right after you turn the catalogue on or
regenerate the token. The token is the part after `/api/v1/opds/`.

## Download chapters over OPDS

In KOReader's file browser, open **OPDS catalog**, add a catalogue and paste the feed URL. From
outside your network, swap the host for the address you use there.

## Sync progress

1. Open a book in KOReader and go to **Tools → Progress sync → Custom sync server**. Enter your
   Maki address followed by `/api/v1/kosync`, for example `http://192.168.1.20:8990/api/v1/kosync`.
   **Settings → Reading → KOReader progress sync** shows the address to copy.
2. Choose **Register / Login**. Enter your **OPDS token** as the username. Type anything as the
   password; Maki doesn't use it.
3. Set **Document matching method** to **Binary**.

**Push progress** and **Pull progress** in the same menu sync by hand. With **Automatically keep
documents in sync** on, KOReader pushes your page as you read and when you close the book or the
device sleeps, and pulls the latest position when a book opens.

What Maki does with a pushed page:

- It uses the same progress path as Maki's own reader, so it counts toward Rewind and achievements
  and reaches your trackers. Fully incognito series record the position and nothing else.
- In a volume archive with several chapters, a page marks the chapters before it read and saves your
  place in the chapter it falls in. If Maki can't tell where chapters start (no chapter markers in
  the archive's page names), the chapters are only marked read once you reach the last page.
- Books that didn't come from Maki get an OK reply like any other push, so KOReader's retry queue
  moves on. Maki holds them for up to a week in case the file shows up, then drops them.
- A page pushed for a file Maki hasn't recognised yet, such as one you just copied over, is kept for
  up to a week and applied once the background pass records the file. It is held in memory only, so
  restarting Maki drops it. Push again from KOReader afterwards.

## How a file is recognised

KOReader identifies a book by a document id. In **Binary** mode it is an MD5 of a few small samples
of the file's bytes, so the copy on the device must be byte-for-byte a file from your library.

- Maki records the id when the OPDS catalogue serves a download, and a background pass records it for
  every library file every six hours, so files copied over USB or a sync tool match too.
- An unknown id makes that pass run sooner, so a file added a moment ago matches on the next sync.
- When Maki rewrites a file (a ComicInfo update, a rename that touches the archive), the ids of the
  old bytes keep working for copies already on a device.

**Filename** mode is an MD5 of the file name. Maki matches the name a file has in your library and
the name the OPDS download was served under. But KOReader names OPDS downloads after the catalogue
entry unless **Use server filenames** is on, and those names are often the same across series ("Ch.
12"). A name that fits more than one file matches nothing, so use Binary.

Regenerating the OPDS token signs KOReader out as well; log in again with the new token.
