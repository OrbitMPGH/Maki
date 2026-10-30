---
title: Activity, Wanted and the queue
description: Where downloads show up, what Wanted means for a chapter, and how to queue the chapters you are missing.
---

## Activity

The **Activity** page (`/activity`) has three tabs.

- **Queue** lists downloads that are waiting, running or need a decision. The counters on top show
  **In progress**, **Queued**, **Needs review** and **Failed**.
- **Upgrades** lists files that fall short of the quality cutoff in their profile. When none do, it
  says **Every file meets its cutoff**.
- **Upcoming** shows each series' likely next chapter on a five-week grid, as a span with a
  confidence, not a date. A release time that a source announces appears on its day.

The queue updates live. Each row can be moved to the top, moved up or down, retried or
removed. Rows in **Needs review** also get a **Review** button. **Clear queue** removes everything
still pending and cancels downloads in progress. Finished and cancelled downloads move to
**History** below the queue.

A long queue shows only the first items. A line under the table gives the total, and the rest
still download.

### Pausing downloads

**Pause downloads** stops new items from starting. Pick all sources or one source, and a duration:
**Until I resume**, **For 1 hour**, **Until tomorrow morning** or **Until a time I choose**. Queue
rows are untouched and downloads already in progress finish.

## Wanted

Every chapter has a **Wanted** flag that records whether you want a file for it.

Maki sets the flag when it first finds a chapter, based on the series' monitor setting: **All
chapters**, **Smart**, **Main only, no specials**, or **None**. Smart keeps new chapters wanted and
queues them a batch at a time, ahead of where you are reading. A monitor setting only affects
chapters released after you change it.

On a series page, filter the chapter list to **Wanted (N)** (or use the **Wanted** column), and
select chapters to choose **Want** or **Don't want**. Unwanted chapters are skipped by every
download that queues "all wanted" chapters, and by Smart.

The **Clean up read chapters** card under **Settings → Downloads** can delete a file you have
finished. Its chapter stays listed as removed and is not downloaded again unless you turn **Wanted**
back on.

## Downloading what is missing

On a series page, **Download N wanted** (N is the number of wanted chapters without a file) queues
every missing chapter Maki can find. It is hidden when every wanted chapter is on disk. With 50 or
more missing, Maki asks you to confirm first.

The menu next to it has **Download the next**: the next 10 or 25 chapters, or a count you type
under **Next...**. They are queued in chapter order from the lowest missing number, and the rest
stay wanted.

In the library, select series and choose **Search missing** from the bulk actions to search for
each one. It only appears if your account may download chapters.
