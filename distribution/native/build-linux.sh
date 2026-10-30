#!/usr/bin/env bash
# Builds the Linux tarball for the machine's own architecture: Maki.Api self-contained, the web UI,
# a sample systemd unit and a short README, checked with --selftest and a real start.
#
# Run it on the architecture it targets (the release workflow uses an arm64 runner for arm64).
# Cross-publishing works, but the self-test would then run nothing, and the self-test is the point:
# pdfium and libvips load lazily, so a tarball missing them starts fine and fails on the first PDF.
# Building on Linux also keeps the executable bits that a tar made on Windows would lose.
#
# Usage: distribution/native/build-linux.sh [version] [frontend-dist]
#   version        X.Y.Z without the v, default 0.0.1-local
#   frontend-dist  an already built frontend/dist; omitted, the frontend is built here
set -euo pipefail

version="${1:-0.0.1-local}"
version="${version#v}"
dist="${2:-}"
repo="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$repo"

case "$(uname -m)" in
  x86_64) rid=linux-x64 ;;
  aarch64 | arm64) rid=linux-arm64 ;;
  *) echo "unsupported architecture: $(uname -m)" >&2; exit 1 ;;
esac

commit="${SOURCE_COMMIT:-$(git rev-parse HEAD 2>/dev/null || echo unknown)}"
out="$repo/out/native/$rid"
app="$out/maki"
rm -rf "$out"
mkdir -p "$app"

if [ -z "$dist" ]; then
  echo "== Building the frontend =="
  (cd frontend && npm ci --no-audit --no-fund && npm run build)
  dist="frontend/dist"
fi

echo "== Publishing Maki.Api ($rid, self-contained) =="
dotnet publish src/Maki.Api/Maki.Api.csproj -c Release -r "$rid" --self-contained true \
  -p:SkipFrontendBuild=true -p:Version="$version" -p:InformationalVersion="$version+$commit" -o "$app"
cp -r "$dist" "$app/wwwroot"
cp LICENSE THIRD_PARTY_NOTICES.md "$app/"
cp distribution/native/maki.service "$app/"
cp distribution/native/README-linux.txt "$app/README.txt"
chmod +x "$app/Maki.Api"
# The browser installer runs this as whichever user the service uses, not the one who unpacked it.
chmod a+rx "$app"/.playwright/node/*/node

echo "== Self-test =="
"$app/Maki.Api" --selftest

echo "== Start against an empty config folder =="
config="$(mktemp -d)"
printf '{ "Port": 8997 }' > "$config/config.json"
MAKI_CONFIG_DIR="$config" "$app/Maki.Api" > "$out/smoke.log" 2>&1 &
pid=$!
trap 'kill "$pid" 2>/dev/null || true; rm -rf "$config"' EXIT
ok=0
for _ in $(seq 1 120); do
  sleep 1
  if ! kill -0 "$pid" 2>/dev/null; then
    echo "Maki.Api exited during startup:" >&2
    cat "$out/smoke.log" >&2
    exit 1
  fi
  if curl -fsS "http://localhost:8997/initialize.json" > /dev/null 2>&1; then ok=1; break; fi
done
if [ "$ok" -ne 1 ]; then
  echo "Maki.Api did not answer /initialize.json within two minutes" >&2
  cat "$out/smoke.log" >&2
  exit 1
fi
curl -fsS "http://localhost:8997/" | grep -q '<div id="root">' || { echo "The SPA shell was not served" >&2; exit 1; }
echo "Started and served the SPA"
kill "$pid"
wait "$pid" 2>/dev/null || true
rm -f "$out/smoke.log"

archive="$out/Maki-$version-$rid.tar.gz"
tar -C "$out" -czf "$archive" maki
echo "Built $archive ($(du -h "$archive" | cut -f1))"
