Maki for Linux (community build)

This build is not tested on every distribution. Docker is the supported way to run Maki on a
server; this tarball is for machines without Docker, such as a Proxmox LXC container.

Quick start
  ./Maki.Api
  then open http://localhost:8990 and create the administrator account.

Maki keeps its database, settings and logs in ~/.config/Maki unless MAKI_CONFIG_DIR says
otherwise. The first start downloads the MangaBaka metadata database: about 350 MB to download
and 3.5 GB on disk.

Running it as a service
  sudo useradd --system --home /var/lib/maki --create-home maki
  sudo cp -r . /opt/maki && sudo chown -R maki:maki /opt/maki
  sudo cp maki.service /etc/systemd/system/
  sudo systemctl daemon-reload && sudo systemctl enable --now maki
The service needs read and write access to your manga library folders.

Sources that need a browser
  MangaFire and TopManhua download a headless browser (about 100 MB) the first time they are
  used. The browser needs some system libraries. If Health reports that it will not start,
  install them once with:
    sudo ./.playwright/node/linux-x64/node ./.playwright/package/cli.js install-deps chromium-headless-shell
  (use linux-arm64 on an ARM machine). Both sources also need FlareSolverr.

Updating
  Maki tells you in Settings when a new version is out. Stop the service, replace the files in
  /opt/maki with the new tarball's, and start it again. Your data in the config folder is kept.
