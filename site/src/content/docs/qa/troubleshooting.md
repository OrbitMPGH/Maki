---
title: Troubleshooting and logs
description: Where logs are, how to raise the log level, the Health page, memory use, permission errors and bug reports.
---

### Where are the logs?

In `/config/logs` (`./maki-config/logs` on the host in the compose example), or `%LOCALAPPDATA%\Maki\logs` for the Windows app. Maki writes `maki-<date>.log` per day and starts a new file when one reaches its size limit. The same lines go to the console, so `docker logs maki` works too.

### How do I get more detail in the logs?

Edit `config.json` in the config folder and restart Maki, since logging settings are read at startup. They have no page in the interface.

| Key | Default | Values |
|---|---|---|
| `LogLevel` | `Information` | Lower it, for example `Debug` |
| `HttpRequestLogging` | `Minimal` | `Off`, `Minimal`, `Full` |
| `SlowRequestMs` | `1000` | |
| `LogFileSizeMb` | `20` | |
| `RetainedLogFiles` | `7` | |

### What is the Health page?

An admin page (**Health** in the sidebar) that checks your library for missing, unreadable, duplicate and suspicious files. Scans only read files. Repeated findings of one kind fold into one row, cleared with **Acknowledge all**. Schedule and thresholds are under **Settings → System → Library health**. See
[Files and library](/Maki/features/files-and-library/).

### Maki uses a lot of memory. Is that a problem?

Usually not. Most of it is the kernel's file cache, which dashboards count as container memory and the kernel returns when the host needs it. Use `mem_reservation` rather than a hard limit, and keep `mem_limit` at 1.5 GB or more. Admins can see managed, native and page cache memory at `/api/v1/system/memory`. See [Memory](/Maki/installation/docker-compose/#memory).

### Why do I get permission denied errors?

Maki runs as `PUID`/`PGID`. It fixes ownership under `/config` but not your library folder, so that user needs read and write access there. Check the library folder's owner on the host and set `PUID` and `PGID` to match. See
[Install with Docker Compose](/Maki/installation/docker-compose/).

### What should I include in a bug report?

Use **Report a bug** in the sidebar footer. The details to include are in [Ways to help](/Maki/contribution/ways-to-help/#report-a-bug). Remove API keys, passwords and tokens from logs first. Questions that are not bugs go to [GitHub Discussions](https://github.com/OrbitMPGH/Maki/discussions).
