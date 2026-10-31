---
title: Install on Windows
description: Install Maki with the Windows installer. A tray icon keeps it running, installs updates, and keeps your library in your user profile.
---

The Windows app runs Maki on your own computer without Docker. It is the same program as the Docker image. A tray icon keeps it running in the background and installs updates.

## Requirements

- Windows 10 or 11, 64-bit.
- Room for the MangaBaka metadata database: about 350 MB to download and 3.5 GB on disk, on top of your library.

## Install

1. Download `Maki-<version>-windows-x64-setup.exe` from [GitHub releases](https://github.com/OrbitMPGH/Maki/releases) and run it. It installs for your account only and does not ask for administrator rights.
2. On the folder page, keep the default or pick another folder. The default is `%LOCALAPPDATA%\Programs\Maki`.

   <!-- screenshot: the installer's folder page -->

3. If Windows shows **Windows protected your PC**, the installer is not code-signed yet. Click **More info**, then **Run anyway**. You only see this on the first install. Updates do not show it.
4. Maki opens in your browser at `http://localhost:8990` once it is ready. Create the administrator account and work through the setup guide.

Maki starts when you sign in. To start it yourself instead, clear **Start with Windows** in the tray menu.

### Choosing the folder

The installer refuses a few folders, because uninstalling deletes the install folder and everything in it:

- A folder that already has files in it, unless it is an existing Maki install.
- A drive root.
- `%LOCALAPPDATA%\Maki`, where your data lives, or a folder that contains it.
- A folder you cannot write to without administrator rights.

If Maki is already installed, the installer offers that folder. Install over it to update, or uninstall first to move Maki somewhere else. Your library and settings stay where they are either way.

The portable zip (`Maki-<version>-windows-x64-portable.zip`) is the same app without the Start menu entry and the uninstaller. Unzip it to a permanent folder, not Downloads, and run `Maki.exe`.

## Tray icon

Left-click the icon to open Maki. Right-click it for the menu:

<!-- screenshot: the tray icon's right-click menu, with a pending update showing -->

- **Open Maki**: opens the web app.
- **Start Maki**: only shows when Maki has stopped.
- **Restart to update to <version>**: only shows once a new version has downloaded.
- **Start with Windows**: starts Maki when you sign in.
- **View logs**: opens the logs folder. Start here when something goes wrong.
- **Quit Maki**: stops Maki. This can take up to 30 seconds while Maki finishes background work.

Closing the browser tab does not stop Maki. It keeps checking for new chapters until you quit from the icon.

## Reach it from other devices

By default only this computer can open Maki. To read on a phone over your home Wi-Fi:

1. Open Maki and go to **Settings → System → Network**.
2. Turn on **Allow other devices on your network**, then click **Save**.
3. Click **Restart now**. The change applies after Maki restarts.
4. On the phone, open `http://<this computer's address>:8990`. To find the address, run `ipconfig` and use the IPv4 address of your Wi-Fi adapter.

If the page does not load, Windows Firewall is probably blocking it. Windows usually asks the first time Maki listens on the network. If you cancelled that prompt, allow Maki under **Windows Security → Firewall & network protection → Allow an app through firewall**.

Offline reading and push notifications need HTTPS, which a plain address on your network does not give you. See [Reverse proxy and HTTPS](/Maki/installation/reverse-proxy-https/).

## Where your data lives

Maki keeps its database, settings, covers, backups and logs in `%LOCALAPPDATA%\Maki`, separate from the program folder. Updating Maki does not touch that folder. The logs are in `%LOCALAPPDATA%\Maki\logs`. The full list of files is in [Ports, paths and files](/Maki/reference/ports-and-paths/).

## Updates

Maki checks for a new version two minutes after it starts, then every six hours. Updates download in the background. When one is ready, the tray menu shows **Restart to update to <version>**, and **Settings → System → Updates** shows the same. Choose it when it suits you. If you do not, Maki installs the update the next time it starts.

Maki is offline for about a minute while it installs. Only stable releases install automatically. Release candidates are for people who want to test them, and you install those by hand from [Releases](https://github.com/OrbitMPGH/Maki/releases).

Before an update changes the database, Maki takes a backup. If an update goes wrong, install the previous version from Releases and restore that backup under **Settings → System → Backup & restore**. See [Upgrading](/Maki/installation/upgrading/) for more.

## Uninstall

1. Open Windows **Settings → Apps**, select Maki, and choose **Uninstall**.
2. Maki stops, its Start with Windows entry is removed, and the install folder is deleted.

Uninstalling keeps `%LOCALAPPDATA%\Maki`, so a reinstall picks up where you left off. Delete that folder too if you want everything gone.

## Sources that need extra help

- Some sources sit behind Cloudflare and need [FlareSolverr](/Maki/guides/sources/flaresolverr/). Without it, those sources stay unavailable and Health says so.
- MangaFire and TopManhua drive a headless browser. Maki downloads it the first time one of those sources needs it.

## If Maki will not start

- **"Another program is using port 8990."** Something else is using the port, often a second copy of Maki. Close it, or change `Port` in `%LOCALAPPDATA%\Maki\config.json`, then choose **Start Maki** from the tray icon.
- **"Maki stopped and could not start again."** Choose **View logs** and open the newest file.

## Next

[First-run setup](/Maki/installation/first-run/).
