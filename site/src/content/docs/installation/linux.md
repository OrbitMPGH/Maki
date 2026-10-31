---
title: Install on Linux
description: Run Maki from the Linux tarball without Docker, optionally as a systemd service. Builds for x64 and arm64.
---

Docker is the supported way to run Maki on Linux. Use the tarball where Docker is not an option, such as a Proxmox LXC container or a small NAS. Every release is built and smoke-tested in CI on x64 and arm64. Nobody tests each distribution by hand, so the tarball is a community tier. If Docker is an option, use [Docker Compose](/Maki/installation/docker-compose/).

## Requirements

- A 64-bit glibc distribution, on x64 or arm64. Alpine and other musl systems are not supported.
- About 350 MB to download the MangaBaka database and 3.5 GB on disk, on top of your library.

The tarball carries its own .NET runtime, so Maki itself needs nothing else installed. Browser-based sources need a few system libraries. See [Sources that need a browser](#sources-that-need-a-browser).

## Install

1. Download the tarball for your machine from [GitHub releases](https://github.com/OrbitMPGH/Maki/releases): `Maki-<version>-linux-x64.tar.gz` or `Maki-<version>-linux-arm64.tar.gz`.
2. Unpack it, then start Maki from the new folder:

```bash
tar -xzf Maki-<version>-linux-x64.tar.gz
cd maki
./Maki.Api
```

3. Open `http://<address>:8990` and create the administrator account. The setup guide opens next.

Linux builds listen on every network interface by default, so other devices can reach Maki at once. To keep it to this machine, turn off **Allow other devices on your network** under **Settings → System → Network**, then restart Maki.

To check the bundled libraries and web files without starting the server, run `./Maki.Api --selftest`.

## Run it as a service

The tarball includes `maki.service`, a sample systemd unit. It runs Maki as a `maki` user from `/opt/maki`, with its data in `/var/lib/maki`. Run these from the unpacked `maki` folder:

```bash
sudo useradd --system --home /var/lib/maki --create-home maki
sudo mkdir -p /opt/maki && sudo cp -r . /opt/maki && sudo chown -R maki:maki /opt/maki
sudo cp maki.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now maki
```

The `maki` user needs read and write access to your library folders.

The unit restarts Maki whenever it stops. Maki stops itself on purpose after a restore or a restart from **Settings**, and the unit starts it again. Without the service, a stopped Maki stays down until you start it by hand.

## Sources that need a browser

MangaFire and TopManhua drive a headless browser. Maki downloads it the first time one of those sources needs it. The browser also needs some system libraries. If **Health** reports that it will not start, install them once:

```bash
sudo /opt/maki/.playwright/node/linux-x64/node /opt/maki/.playwright/package/cli.js install-deps chromium-headless-shell
```

On an arm64 machine, use `linux-arm64` in the path. If you run Maki from the unpacked folder instead of `/opt/maki`, use that folder's path. Both sources also need [FlareSolverr](/Maki/guides/sources/flaresolverr/).

## Where your data lives

Without the service, Maki keeps its data in `~/.config/Maki`. With the service, it is `/var/lib/maki`. Set `MAKI_CONFIG_DIR` to put it somewhere else. The database, `config.json`, covers, backups and logs all live there. The full list is in [Ports, paths and files](/Maki/reference/ports-and-paths/).

Keep the data folder out of the app folder. Updates replace `/opt/maki`, and they leave the data folder alone.

## Updates

The tarball does not update itself. When a new version is out, Maki shows **Update available** under **Settings → System → Updates**. To update, download the new tarball and replace the app folder. Your data stays where it is.

```bash
sudo systemctl stop maki
tar -xzf Maki-<version>-linux-x64.tar.gz
sudo rm -rf /opt/maki
sudo cp -r maki /opt/maki
sudo chown -R maki:maki /opt/maki
sudo systemctl start maki
```

Without the service, stop Maki, replace the unpacked folder, and start it again. Before an update changes the database, Maki takes a backup. If an update goes wrong, install the previous tarball and restore that backup under **Settings → System → Backup & restore**. See [Upgrading](/Maki/installation/upgrading/) for more.

## Next

[First-run setup](/Maki/installation/first-run/).
