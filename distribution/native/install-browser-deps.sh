#!/bin/sh
# Installs the system libraries and fonts the headless browser (MangaFire, TopManhua) needs on a
# Debian or Ubuntu system. The Docker image runs it at build time; a native install runs it once:
#   sudo /opt/maki/install-browser-deps.sh
# The list is the one Playwright 1.61's `install-deps chromium-headless-shell` installed, less xvfb,
# which only a headed browser uses. Ubuntu 24.04 renamed some libraries with a t64 suffix; where a
# name is unknown, the one without the suffix is tried. A library that is found under neither name
# stops the script, since the browser cannot start without it; a missing font is only skipped.
set -eu

if ! command -v apt-get >/dev/null 2>&1; then
    echo "apt-get not found: install the libraries named in this script with your package manager." >&2
    exit 1
fi

libraries="libasound2t64 libatk-bridge2.0-0t64 libatk1.0-0t64 libatspi2.0-0t64 libcairo2 libcups2t64
libdbus-1-3 libdrm2 libgbm1 libglib2.0-0t64 libnspr4 libnss3 libpango-1.0-0 libx11-6 libxcb1
libxcomposite1 libxdamage1 libxext6 libxfixes3 libxkbcommon0 libxrandr2 libfontconfig1 libfreetype6"

fonts="fonts-noto-color-emoji fonts-unifont xfonts-cyrillic xfonts-scalable fonts-liberation
fonts-ipafont-gothic fonts-wqy-zenhei fonts-tlwg-loma-otf fonts-freefont-ttf"

apt-get update

# Prints the name apt knows the package by, or nothing.
resolve() {
    if apt-cache show "$1" >/dev/null 2>&1; then
        echo "$1"
    elif [ "${1%t64}" != "$1" ] && apt-cache show "${1%t64}" >/dev/null 2>&1; then
        echo "${1%t64}"
    fi
}

resolved=""
missing=""
for package in $libraries; do
    name=$(resolve "$package")
    if [ -n "$name" ]; then
        resolved="$resolved $name"
    else
        missing="$missing $package"
    fi
done

if [ -n "$missing" ]; then
    echo "The headless browser needs these libraries, which this system's package lists do not have:$missing" >&2
    exit 1
fi

for package in $fonts; do
    name=$(resolve "$package")
    if [ -n "$name" ]; then
        resolved="$resolved $name"
    else
        echo "skipping font $package: not in this system's package lists" >&2
    fi
done

# shellcheck disable=SC2086
DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends $resolved
