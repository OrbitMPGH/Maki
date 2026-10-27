---
title: Troubleshooting and logs
description: Where logs are, how to raise the log level, the Health page, memory use, permission errors and bug reports.
---

### Where are the logs?

With Docker, they are in the `logs` folder inside `/config`, so `./maki-config/logs` on the host in the
compose example. On Windows they are in `%APPDATA%\Maki\logs`. Maki writes one file per day named
`maki-<date>.log`, and starts a new file when a day's file reaches its size limit. The same lines also
go to the console, so `docker logs maki` shows them too.

### How do I get more detail in the logs?

Edit `config.json` in the config folder and restart Maki, because the logging settings are read once at
startup. `LogLevel` is `Information` by default and can be set lower, such as `Debug`. The other keys are
`HttpRequestLogging` (`Minimal` by default, or `Off` or `Full`), `SlowRequestMs` (1000), `LogFileSizeMb`
(20) and `RetainedLogFiles` (7). These settings have no page in the interface.

### What is the Health page?

The Health page, under **Health** in the sidebar, is for admins. It checks your library for missing,
unreadable, duplicate and suspicious files. Scans only read files and never change them. Repeated
findings of one kind fold into one row, which you can clear with **Acknowledge all**. The scan schedule
and thresholds are under **Settings → System → Library health**. See
[Files and library](/Maki/features/files-and-library/).

### Maki uses a lot of memory. Is that a problem?

Most of the number is usually the kernel's file cache, which dashboards count as the container's memory.
The kernel gives that cache back when the host needs the memory. Set `mem_reservation` instead of a hard limit, and
do not set `mem_limit` below 1.5 GB. Admins can see a breakdown of managed memory, native memory and page
cache at `/api/v1/system/memory`. See [Memory](/Maki/installation/docker-compose/#memory).

### Why do I get permission denied errors?

Maki runs as the user named by `PUID` and `PGID`. At startup it fixes ownership under `/config`, but it
does not change your library folder, so that user must be able to read and write the library. If a file
cannot be moved or deleted, Maki says it was locked or permission was denied. Check the owner of the
library folder on the host, and set `PUID` and `PGID` to match it. See
[Install with Docker Compose](/Maki/installation/docker-compose/).

### What should I include in a bug report?

Use **Report a bug** in the sidebar footer, which opens GitHub's issue form. Include what happened, what
you expected, your Maki version, and how it is installed (Docker, bare install, built from source, or not
sure). Add the relevant lines from the log, and remove API keys, passwords and tokens first. Questions
that are not bugs belong in [GitHub Discussions](https://github.com/OrbitMPGH/Maki/discussions).
