#!/usr/bin/env bash
# Builds libjxl's shared libraries for the server upscaler's JPEG XL output (ImageCodec.EncodeUpscaled).
# The libvips Maki bundles (NetVips.Native) has no JPEG XL, so Maki calls libjxl directly.
#
# Usage: distribution/libjxl/build.sh <out-dir> [extra cmake arguments]
#   Extra arguments go to cmake's configure step, for a cross toolchain in the Dockerfile.
# Needs git, cmake, ninja and a C++17 compiler. The libraries land flat in <out-dir> with an
# $ORIGIN rpath, so libjxl finds libjxl_cms beside it in Maki's publish folder.
set -euo pipefail

version=0.12.0
commit=a7a9c787341cf703dede03c2009fa460cae5e5df

out="$(mkdir -p "$1" && cd "$1" && pwd)"
shift
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

git clone --quiet --depth 1 --branch "v$version" --recurse-submodules --shallow-submodules \
  https://github.com/libjxl/libjxl.git "$work/src"
actual="$(git -C "$work/src" rev-parse HEAD)"
if [ "$actual" != "$commit" ]; then
  echo "libjxl v$version is $actual, expected $commit" >&2
  exit 1
fi

cmake -S "$work/src" -B "$work/build" -G Ninja \
  -DCMAKE_BUILD_TYPE=Release \
  -DBUILD_SHARED_LIBS=ON \
  -DBUILD_TESTING=OFF \
  -DJPEGXL_ENABLE_TOOLS=OFF \
  -DJPEGXL_ENABLE_JPEGLI=OFF \
  -DJPEGXL_ENABLE_DOXYGEN=OFF \
  -DJPEGXL_ENABLE_MANPAGES=OFF \
  -DJPEGXL_ENABLE_BENCHMARK=OFF \
  -DJPEGXL_ENABLE_EXAMPLES=OFF \
  -DJPEGXL_ENABLE_JNI=OFF \
  -DJPEGXL_ENABLE_SJPEG=OFF \
  -DJPEGXL_ENABLE_OPENEXR=OFF \
  -DJPEGXL_ENABLE_PLUGINS=OFF \
  -DJPEGXL_ENABLE_SKCMS=ON \
  -DJPEGXL_FORCE_SYSTEM_BROTLI=OFF \
  -DJPEGXL_FORCE_SYSTEM_HWY=OFF \
  -DJPEGXL_FORCE_SYSTEM_LCMS2=OFF \
  -DCMAKE_INSTALL_RPATH='$ORIGIN' \
  -DCMAKE_BUILD_WITH_INSTALL_RPATH=ON \
  "$@"
# Some of libjxl's files take over 1 GB to compile, so one job per core runs a Docker VM out of memory.
jobs="$(nproc)"
avail_kb="$(awk '/MemAvailable/ { print $2 }' /proc/meminfo 2>/dev/null || echo 0)"
if [ "$avail_kb" -gt 0 ]; then
  by_memory=$(( avail_kb / 1536000 ))
  [ "$by_memory" -lt 1 ] && by_memory=1
  [ "$by_memory" -lt "$jobs" ] && jobs="$by_memory"
fi
cmake --build "$work/build" --target jxl jxl_cms --parallel "$jobs"

# Copy the real files under their sonames, the names the dynamic loader asks for. readelf reads any
# architecture's ELF, so this also works on a cross build.
for lib in $(find "$work/build" -name 'lib*.so.*' -type f); do
  soname="$(readelf -d "$lib" | sed -n 's/.*(SONAME).*\[\(.*\)\]/\1/p')"
  cp "$lib" "$out/${soname:-$(basename "$lib")}"
  ls -l "$out/${soname:-$(basename "$lib")}"
done
