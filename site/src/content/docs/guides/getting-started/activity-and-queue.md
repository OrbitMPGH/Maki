---
title: Activity, Wanted and the queue
description: Where downloads show up, what Wanted means for a chapter, and how to queue the chapters you are missing.
---

Use this page to see what Maki is downloading, what it is still waiting for, and how to queue the
chapters you do not have yet.

## Activity

The **Activity** page (`/activity`) has three tabs.

- **Queue** lists downloads that are waiting, running or need a decision. The counters on top show
  **In progress**, **Queued**, **Needs review** and **Failed**.
- **Upgrades** lists files that fall short of the quality cutoff in their profile. When none do, it
  says **Every file meets its cutoff**.
- **Upcoming** shows each series' likely next chapter on a five-week grid. Maki shows it as a span
  with a confidence, not as a date. A release time that a source announces appears on its day.

The queue updates live. Each row has buttons to move it to the top, move it up or down, retry it,
or remove it. **Clear queue** removes everything still pending and cancels downloads in progress.
Finished and cancelled downloads move to **History** below the queue.

On a long queue the table shows only the first items. A line under it gives the total, and the
rest still download.

### Pausing downloads

**Pause downloads** stops new items from starting. You choose what to pause, all sources or one
source, and for how long: **Until I resume**, **For 1 hour**, **Until tomorrow morning** or
**Until a time I choose**. Pausing does not change any queue row, so nothing is lost. Downloads
already in progress finish.

## Wanted

Every chapter has a **Wanted** flag. It records whether you want a file for that chapter.

Maki sets the flag when it first finds a chapter. The series' monitor setting decides how: **All
chapters**, **Smart**, **Main only, no specials**, or **None**. Smart keeps new chapters wanted and
queues them a batch at a time, ahead of where you are reading. A monitor setting only affects
chapters released after you change it.

You can change the flag yourself on a series page:

- Filter the chapter list to **Wanted (N)** to see only wanted chapters, or use the **Wanted**
  column.
- Select chapters and choose **Want** or **Don't want**.

Chapters you do not want are skipped by every download that queues "all wanted" chapters, and by
Smart.

Read file cleanup can delete a file you have finished. Its chapter then stays listed as removed and
is not downloaded again on its own. Turn **Wanted** back on for that chapter to download it again.

## Downloading what is missing

On a series page, the main download button reads **Download N wanted**, where N is the number of
wanted chapters without a file. It is hidden when every wanted chapter is already on disk. Clicking
it queues every missing chapter Maki can find, and the notice tells you how many it queued. With 50
or more missing, Maki asks you to confirm first.

The menu next to that button has **Download the next**. It queues the next 10 or 25 chapters, or a
count you type under **Next...**. These start from the lowest missing chapter number. The rest stay
wanted and wait for you.

In the library, select series and choose **Search missing** from the bulk actions. Maki runs a
search for each selected series. The action only appears if your account may download chapters.

:::tip
Use **Download the next** when you want a few chapters now. Maki queues them in chapter order and
leaves the rest wanted.
:::
