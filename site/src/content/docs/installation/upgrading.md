---
title: Upgrading
description: How to update Maki under Docker or Unraid, what happens on startup, and how to go back.
---

Maki's database migrations apply on startup, and they cannot be reversed. This page covers how to
update, what Maki does before it changes your data, and how to go back if an upgrade goes wrong.

## Docker

With Docker Compose, pull the new image and recreate the container:

```bash
docker compose pull
docker compose up -d
```

`up -d` recreates the container only when the image changed. Your data stays in the `/config` and
`/library` mounts.

With `docker run`, pull the image, remove the old container and run your command again with the same
`-v` and `-e` values:

```bash
docker pull ghcr.io/orbitmpgh/maki:latest
docker stop maki && docker rm maki
```

To upgrade on purpose instead of following `latest`, pin a version tag such as
`ghcr.io/orbitmpgh/maki:X.Y.Z`. CI publishes `X.Y.Z`, `X.Y` and `X` tags for each release. The `latest`
tag only moves on stable releases.

## Unraid

Update the container from the Docker tab. Unraid pulls the new image and restarts the container with
the settings from the template you already have. See [Install on Unraid](/Maki/installation/unraid/)
and [Unraid paths and appdata](/Maki/installation/unraid-paths/).

## What happens on startup

When a new version starts and the database has migrations it has not applied yet, Maki does the following:

1. It takes a backup of the database and settings into the `backups` folder under `/config`.
2. It applies the migrations.
3. It starts the server.

The backup comes first because migrations are forward-only. There is no down migration. Once a migration
has run, the only way back is to restore a backup taken before it.

If a database was written by a newer Maki than the one running, Maki logs an error and says so. Running an
older build against a newer database can corrupt data, so restore the pre-upgrade backup or upgrade again.

## Going back

To roll back to the version you had before:

1. Start the older image. Pin its tag, such as `ghcr.io/orbitmpgh/maki:X.Y.Z`, in your compose file or
   on the `docker run` line.
2. Restore the backup taken just before the upgrade. Restoring replaces the database and settings, then
   restarts Maki. See [Backup and restore](/Maki/guides/running/backup-restore/).

Restoring a backup taken by a newer version is refused, so use the pre-upgrade backup. Some changes made after
that backup are kept on restore, such as revoked API keys. Passwords, SSO links and other account changes
go back to the backup's state.

## Upgrading from a single-user Maki

The first page asks you to set a username and password. Your library, history and tracker connections are
already attached to that account.
