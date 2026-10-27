---
title: Health checks
description: What the Health page checks, how often it checks, and what to do about each kind of finding.
---

The Health page lists problems Maki has noticed in your setup and your files. Only admins can open it, because it shows file paths. The Health item in the sidebar shows how many problems are open, and turns red when one of them is an error.

## When checks run

- System checks run every 15 minutes. **Check now** runs them straight away.
- Custom sources are probed every six hours. A probe can take minutes, so it runs less often than the other checks.
- File scans run once a day, at the hour set by **Daily scan hour**. New files are analysed as they arrive. To stop the daily scan, turn off **Analyze new files and run daily reconciliation** under **Settings → System → Library health**.

## What the system checks cover

The system checks are grouped under Library, Storage, Connections, System, Downloads and Scheduled jobs.

- **Library:** root folders that cannot be reached, series with no source mapping, and sources that keep failing.
- **Storage:** whether each library and download folder is writable, and how much free space each drive has. A drive turns to warning or error when its free space falls below the thresholds in **Library health**, either as a percentage or in GiB.
- **Connections:** whether download clients, indexers, Kavita, FlareSolverr and Suwayomi answer. Also notification connections that keep failing, custom sources that no longer match their site, and the MangaBaka dump (missing, damaged or stale).
- **System:** backup freshness, the scheduler, the database and the migration history. A backup is fresh if it is newer than the **Backup freshness** threshold, 7 days by default.
- **Downloads:** failed downloads, and sources cooling down after failures.
- **Scheduled jobs:** background jobs that are failing.

File problems are separate. The **Files** tab lists each archive with its findings: missing, empty, corrupt, no pages, damaged image, duplicate, not linked, size mismatch, ambiguous names, or unreadable.

## Scan the files

Open **Scan files** and choose one of two scans:

- **Index** finds missing, new or broken files. It takes seconds.
- **Verify** reads every byte to catch rotted files. Expect about half an hour per 100 GB.

Scans only cover one root folder when the Files tab has one selected, and the menu says which.

## Act on a finding

Each system check has buttons on its row:

- **Open** goes to the place that fixes it. A custom source opens its setup wizard, and a notification connection opens its settings.
- **Acknowledge** hides the problem from the header count, and **Reopen** brings it back. Acknowledging means "stop telling me", not "this is fine". The check stays on the page, and if its status changes, it counts again.
- **Show passing** lists the checks that are healthy.
- A source outage row has **Refresh all**, which re-checks every series on that source. Use it once the site is back, so chapters released during the outage get queued.

For files:

- Review an archive to see its findings and decide what to do.
- **Import unlinked** imports archives that are not yet linked to a chapter.
- Once you have decided what a finding means, mark it with **Ignore** or **Acknowledge**.
- A file that is missing while its chapter still points at it stays a finding. Until you deal with it, the chapter shows as downloaded.

The **Repairs** tab lists repair and delete requests and their status. A failed request can be retried or dismissed. A PDF cannot be repaired, because Maki has no other copy to replace it from.

## Tune the checks

Click the gear next to **Check now**, or open **Settings → System → Library health**. You can change the low disk thresholds (percentage and GiB, for warning and error), backup freshness in days, the daily scan hour, the time zone, and how many pages are decoded at once.
