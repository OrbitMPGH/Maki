---
title: Health checks
description: What the Health page checks, how often it checks, and what to do about each kind of finding.
---

The Health page lists problems Maki has noticed in your setup and files. Only admins can open it,
because it shows file paths. The sidebar item shows the open problem count and turns red when one is
an error.

## When checks run

- System checks run every 15 minutes. **Check now** runs them immediately.
- Custom sources are probed every six hours, since a probe can take minutes.
- File scans run once a day at **Daily scan hour**. New files are analysed as they arrive. To stop
  the daily scan, turn off **Analyze new files and run daily reconciliation** under **Settings →
  System → Library health**.

## What the system checks cover

- **Library:** unreachable root folders, series with no source mapping, and sources that keep
  failing.
- **Storage:** whether each library and download folder is writable, and free space per drive. A
  drive turns to warning or error below the thresholds in **Library health**, as a percentage or in
  GiB.
- **Connections:** whether download clients, indexers, Kavita, FlareSolverr and Suwayomi answer.
  Also notification connections that keep failing, custom sources that no longer match their site,
  and the MangaBaka dump (missing, damaged or stale).
- **System:** backup freshness, the scheduler, the database and the migration history. A backup is
  fresh if newer than the **Backup freshness** threshold, 7 days by default.
- **Downloads:** failed downloads, and sources cooling down after failures.
- **Scheduled jobs:** background jobs that are failing.

File problems are separate. The **Files** tab lists each archive with its findings: missing, empty,
corrupt, no pages, damaged image, duplicate, not linked, size mismatch, ambiguous names, or
unreadable.

## Scan the files

**Scan files** offers two scans. If the Files tab has a root folder selected, the scan covers only
that folder, and the menu says so.

- **Index** finds missing, new or broken files. It takes seconds.
- **Verify** reads every byte to catch rotted files. Expect about half an hour per 100 GB.

## Act on a finding

System checks have these buttons:

- **Open** goes to where it is fixed, such as a custom source's setup wizard or a notification
  connection's settings.
- **Acknowledge** hides the problem from the header count, and **Reopen** brings it back. It means
  "stop telling me", not "this is fine". The check stays on the page, and counts again if its status
  changes.
- **Show passing** lists the healthy checks.
- A source outage row has **Refresh all**, which re-checks every series on that source. Use it once
  the site is back, so chapters released during the outage get queued.

For files:

- Review an archive to see its findings and decide what to do.
- **Import unlinked** imports archives not yet linked to a chapter.
- Mark a finding with **Ignore** or **Acknowledge** once you have decided what it means.
- A file that is missing while its chapter still points at it stays a finding, and the chapter shows
  as downloaded until you deal with it.

The **Repairs** tab lists repair and delete requests and their status. Retry or dismiss a failed one.
A PDF cannot be repaired, because Maki has no other copy to replace it from.

## Tune the checks

Click the gear next to **Check now**, or open **Settings → System → Library health**. You can change
the low disk thresholds (percentage and GiB, for warning and error), backup freshness in days, the
daily scan hour, the time zone, and how many pages are decoded at once.
