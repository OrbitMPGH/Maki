# Maki on Windows

The Windows app runs Maki on your own computer, with no Docker or server needed. It is the same Maki
as the Docker image. A small icon in the notification area keeps it running and installs updates.

## Install

1. Download `Maki-<version>-windows-x64-setup.exe` from
   [Releases](https://github.com/OrbitMPGH/Maki/releases) and run it. It installs for your account
   only and does not ask for admin rights.
2. Windows may show **Windows protected your PC**, because the installer is not code-signed yet.
   Click **More info**, then **Run anyway**. This only happens on the first install: updates are
   downloaded by Maki itself and do not show it.
3. Maki opens in your browser at `http://localhost:8990`. Create the administrator account and
   follow the setup guide.

Maki starts with Windows from then on. Untick **Start with Windows** in the icon's menu if you
would rather start it yourself from the Start menu.

The portable zip (`Maki-<version>-windows-x64-portable.zip`) is the same app without the Start menu
entry or uninstaller. Unzip it somewhere permanent, not inside Downloads, and run `Maki.exe`.

## What it needs

- Windows 10 or 11, 64-bit.
- About 4 GB of free disk space before your library: Maki downloads the MangaBaka metadata database
  on first start (about 350 MB, 3.5 GB once unpacked).
- 300 to 400 MB of memory while running.

Maki keeps its database, settings, backups and logs in `%LOCALAPPDATA%\Maki`. Uninstalling the app
leaves that folder alone, so reinstalling picks up where you left off. Delete it yourself if you
want everything gone.

## The tray icon

Left-click the icon to open Maki. Right-click it for:

- **Start with Windows**: start Maki when you sign in.
- **View logs**: opens the log folder, which is the first thing to look at if something goes wrong.
- **Restart to update**: shows up once a new version has downloaded.
- **Quit Maki**: stops Maki properly. Downloads in progress stop too.

Closing the browser tab does not stop Maki. It keeps checking for new chapters in the background
until you quit it from the icon.

## Updates

Maki checks for new versions a few times a day and downloads them in the background. When one is
ready, choose **Restart to update** from the icon, or from **Settings > System > Updates**. Maki is
offline for about a minute while it installs. Release candidates are never installed automatically.

Before an update changes the database, Maki takes a backup of it. If an update goes wrong, install
the previous version from Releases and restore that backup from **Settings > System > Backup &
restore**.

## Using Maki from your phone or another computer

By default only this computer can open Maki. To read on your phone over your home Wi-Fi, turn on
**Allow other devices on your network** under **Settings > System > Network** and restart Maki.
Then open `http://<this computer's address>:8990` on the phone.

If nothing loads, Windows Firewall is probably blocking it. Windows asks the first time Maki listens
on the network; if that prompt was cancelled, allow Maki under **Windows Security > Firewall &
network protection > Allow an app through firewall**.

Offline reading and push notifications need HTTPS, which a plain address on your network is not.
See [Exposing Maki to the internet](exposing-to-the-internet.md).

## Sources that need extra help

- **FlareSolverr.** Several sources sit behind Cloudflare and need
  [FlareSolverr](https://github.com/FlareSolverr/FlareSolverr), which has its own Windows download.
  Run it, then set its address (usually `http://localhost:8191`) under **Settings > Downloads >
  FlareSolverr**. Without it those sources are unavailable and Health says so. The rest work
  without it.
- **A headless browser.** MangaFire and TopManhua also drive a browser. Maki downloads it (about
  100 MB) the first time one of them needs it, and Health shows the download.

## When something goes wrong

- **"Another program is using port 8990."** Something else already uses that port, often a second
  copy of Maki or a Docker container of it. Close it, or change `Port` in
  `%LOCALAPPDATA%\Maki\config.json`, then choose **Start Maki** from the icon.
- **Maki stopped and could not start again.** Choose **View logs** and look at the end of the
  newest file.
- **The laptop went to sleep.** Maki carries on when it wakes up. Scheduled checks it missed run
  then, and an interrupted download is retried.
