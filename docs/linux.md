# Maki on Linux without Docker

Docker is the supported way to run Maki on Linux. The tarballs on
[Releases](https://github.com/OrbitMPGH/Maki/releases) are for machines where Docker is not an
option, such as a Proxmox LXC container or a small NAS. They are a community tier: every release
is built and smoke-tested in CI on x64 and arm64, but not tested by hand on each distribution.

## Install

```bash
tar -xzf Maki-<version>-linux-x64.tar.gz
cd maki
./Maki.Api
```

Then open `http://<address>:8990` and create the administrator account. The tarball carries its own
.NET runtime, so nothing else needs installing. glibc distributions only; Alpine and other musl
systems are not supported.

Data lives in `~/.config/Maki` unless `MAKI_CONFIG_DIR` points elsewhere. The first start downloads
the MangaBaka metadata database (about 350 MB, 3.5 GB once unpacked).

## Run it as a service

The tarball includes `maki.service`, a sample systemd unit that runs Maki as a `maki` user from
`/opt/maki` with its data in `/var/lib/maki`:

```bash
sudo useradd --system --home /var/lib/maki --create-home maki
sudo mkdir -p /opt/maki && sudo cp -r . /opt/maki && sudo chown -R maki:maki /opt/maki
sudo cp maki.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now maki
```

Give the `maki` user read and write access to your library folders. The unit restarts Maki
whenever it stops, which is what brings it back after a restore or a restart from Settings.

## Sources that need a browser

MangaFire and TopManhua drive a headless browser, which Maki downloads (about 100 MB) the first
time either needs it. The browser needs some system libraries. If Health reports that it will not
start, install them once:

```bash
sudo /opt/maki/.playwright/node/linux-x64/node /opt/maki/.playwright/package/cli.js install-deps chromium-headless-shell
```

Use `linux-arm64` in that path on an ARM machine. Both sources also need
[FlareSolverr](https://github.com/FlareSolverr/FlareSolverr).

## Update

Maki tells you under **Settings > System > Updates** when a new version is out. It does not update
itself on Linux. Stop the service, replace the files in `/opt/maki` with the new tarball's, and
start it again. Your data stays in the config folder, and Maki backs up the database before any
update changes it.
