---
title: Upgrading
description: How to update Maki under Docker, Unraid, the Windows app or the Linux tarball, what happens on startup, and how to go back.
---

Maki's database migrations apply on startup, and they cannot be reversed.

## Docker

With Docker Compose, pull the new image and recreate the container:

```bash
docker compose pull
docker compose up -d
```

`up -d` recreates the container only when the image changed. Your data stays in the `/config` and
`/library` mounts.

With `docker run`, pull the image, remove the old container and run your original command again:

```bash
docker pull ghcr.io/orbitmpgh/maki:latest
docker stop maki && docker rm maki
```

To upgrade on purpose instead of following `latest`, pin a version tag such as
`ghcr.io/orbitmpgh/maki:X.Y.Z`. CI publishes `X.Y.Z`, `X.Y` and `X` tags for each release. The `latest`
tag only moves on stable releases.

## Unraid

Update the container from the Docker tab. Unraid keeps the settings from your template. See
[Install on Unraid](/Maki/installation/unraid/) and
[Unraid paths and appdata](/Maki/installation/unraid-paths/).

## Windows app

The Windows app checks for a new version two minutes after it starts, then every six hours, and
downloads it in the background. When one is ready, choose **Restart to update to <version>** from
the tray icon, or use **Settings → System → Updates**. If you do not, Maki installs it the next time it starts. Release candidates are never
installed on their own.

Your database, settings and logs are in `%LOCALAPPDATA%\Maki`, outside the install folder, so an
update leaves them alone. The backup under [What happens on startup](#what-happens-on-startup)
applies to the app too. See [Windows app](/Maki/installation/windows/).

## Linux tarball

The tarball does not update itself. Stop the service, unpack the new tarball and replace
`/opt/maki` with it. Your data stays in `/var/lib/maki`. Then start the service again:

```bash
sudo systemctl stop maki
tar -xzf Maki-<version>-linux-x64.tar.gz
sudo rm -rf /opt/maki
sudo cp -r maki /opt/maki
sudo chown -R maki:maki /opt/maki
sudo systemctl start maki
```

Use `linux-arm64` in the file name on an ARM machine. Maki backs up the database before a new version
applies its migrations, as described under [What happens on startup](#what-happens-on-startup). See
[Linux](/Maki/installation/linux/).

## What happens on startup

When a new version starts with unapplied migrations, Maki:

1. Backs up the database and settings into the `backups` folder under `/config`.
2. Applies the migrations.
3. Starts the server.

The backup comes first because migrations are forward-only. There is no down migration, so once one
has run, the only way back is a backup taken before it.

If the database was written by a newer Maki than the one running, Maki logs an error. Running an
older build against a newer database can corrupt data, so restore the pre-upgrade backup or upgrade
again.

## Going back

To roll back to the version you had before:

1. Start the older image. Pin its tag, such as `ghcr.io/orbitmpgh/maki:X.Y.Z`, in your compose file or
   on the `docker run` line.
2. Restore the backup taken just before the upgrade. Maki stages the restore and exits, then applies
   it on its next start. Something has to start it again: Docker's restart policy, systemd or the
   Windows app. A bare start stays down until you start it. See
   [Backup and restore](/Maki/guides/running/backup-restore/).

Restoring a backup taken by a newer version is refused, so use the pre-upgrade backup. Some changes
made after that backup are kept on restore, such as revoked API keys. Passwords, SSO links and other
account changes go back to the backup's state.

## Upgrading from a single-user Maki

The first page asks you to set a username and password. Your library, history and tracker connections are
already attached to that account.
