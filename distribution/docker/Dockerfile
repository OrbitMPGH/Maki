# --platform=$BUILDPLATFORM pins the build stages to the host's native architecture instead of
# buildx's target platform(s), so nothing builds under qemu emulation. Frontend produces
# platform-independent assets once; backend cross-compiles ReadyToRun native code per TARGETARCH
# via crossgen2 on the host architecture. Only the final runtime stage runs on TARGETPLATFORM.
# ---- Frontend build ----
FROM --platform=$BUILDPLATFORM node:24-alpine AS frontend
WORKDIR /src/frontend
COPY frontend/package.json frontend/package-lock.json ./
# The cache mounts here and on the restore below keep downloaded packages on the build machine
# between builds, so a lockfile change only fetches what changed. CI's gha cache does not carry
# them, which only costs it the download it did anyway.
RUN --mount=type=cache,id=maki-npm,target=/root/.npm npm ci
COPY frontend/ ./
# The message catalogs live at the repo root, not under frontend/, because the API embeds the same
# files. lingui.config.js reaches ../locales, and src/i18n.ts imports each catalog through the
# `@locales` alias that vite.config.ts and tsconfig.app.json both point at that same directory, so
# the build fails without this. It has to land at /src/locales specifically: the alias resolves one
# directory above the package, and WORKDIR here is /src/frontend.
COPY locales/ /src/locales/
RUN npm run build
# Vite emits one lazily-imported catalog-<locale>-*.js chunk per locale from those imports (named in
# vite.config.ts, since client-*.js would also match any other module called client), so whether the
# catalogs actually made it into the bundle is countable. Worth counting because the failure is
# silent in the direction that matters: a bundle missing them still builds, still serves, and
# still answers every string in English. Counted against the locale directories rather than a
# hardcoded number so adding a language cannot leave this checking the old count.
RUN expected="$(ls -1d /src/locales/*/ | wc -l)" \
    && actual="$(ls -1 dist/assets/catalog-*.js 2>/dev/null | wc -l)" \
    && if [ "$actual" -ne "$expected" ]; then \
         echo "Expected $expected compiled client catalogs in the bundle, found $actual." >&2; \
         exit 1; \
       fi

# ---- Backend build ----
# Framework-dependent ReadyToRun publish per TARGETARCH, cross-compiled natively on $BUILDPLATFORM
# via crossgen2 (no qemu). Shipping precompiled native code inside the assemblies moves most code
# pages from dirty anonymous JIT memory (rwxp) to clean file-backed pages that stay out of docker
# stats and can be reclaimed by the kernel without swap. Combined with TieredCompilation=false in
# Maki.Api.csproj, paired cold-cgroup runs measured 26 MiB less anonymous memory after a visit
# (32 MiB at boot) and 7% less CPU, for about 60-70 MB more uncompressed assembly size.
# A RID publish also places only the target architecture's native libraries (libvips, onnxruntime,
# sqlite, pdfium) flat beside Maki.Api.dll instead of shipping ~225 MB of unused OS/arch folders.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
ARG TARGETARCH
WORKDIR /src
COPY Directory.Build.props ./
COPY Maki.sln ./
COPY src/ src/
COPY tests/ tests/
# Imported by Maki.Api.csproj. It only fetches libjxl on Windows; this image gets it from the libjxl stage.
COPY distribution/libjxl/LibJxl.targets distribution/libjxl/
# Maki.Api embeds locales/*/server.po as resources; see the EmbeddedResource item in Maki.Api.csproj.
COPY locales/ locales/

# src/ is copied above, so this layer reruns on every code change; the mount is what makes that cheap.
# publish needs the same mount because it reads the packages from that folder.
RUN --mount=type=cache,id=maki-nuget,target=/root/.nuget/packages \
    case "$TARGETARCH" in \
      amd64) rid=linux-x64 ;; \
      arm64) rid=linux-arm64 ;; \
      *) echo "unknown TARGETARCH: $TARGETARCH" >&2; exit 1 ;; \
    esac \
    && dotnet restore src/Maki.Api/Maki.Api.csproj -r "$rid" /p:PublishReadyToRun=true

# .dockerignore excludes .git, so the version cannot be derived from a tag in here. CI computes it
# from the ref and passes it down. A plain `docker build` gets the -dev default from
# Directory.Build.props, which is the intended tell for an unofficial image. A declared build arg is
# part of the cache key of every RUN after it, and every nightly passes a new version, so declare
# these as late as possible in each stage.
ARG VERSION
ARG SOURCE_COMMIT
RUN --mount=type=cache,id=maki-nuget,target=/root/.nuget/packages \
    case "$TARGETARCH" in \
      amd64) rid=linux-x64 ;; \
      arm64) rid=linux-arm64 ;; \
      *) echo "unknown TARGETARCH: $TARGETARCH" >&2; exit 1 ;; \
    esac \
    && dotnet publish src/Maki.Api/Maki.Api.csproj -c Release -r "$rid" --no-self-contained \
      -o /app/publish /p:UseAppHost=false /p:PublishReadyToRun=true \
      ${VERSION:+/p:Version=$VERSION} \
      ${VERSION:+/p:InformationalVersion=$VERSION${SOURCE_COMMIT:++$SOURCE_COMMIT}}
# Same check for the other half of the catalogs: the EmbeddedResource item names each one
# Maki.Locales.<locale>.po, so the assembly manifest carries those names and a publish that
# embedded nothing is visible here rather than as an English-only container months later.
RUN expected="$(ls -1d locales/*/ | wc -l)" \
    && actual="$(grep -a -o 'Maki\.Locales\.[A-Za-z-]*\.po' /app/publish/Maki.Api.dll | sort -u | wc -l)" \
    && if [ "$actual" -ne "$expected" ]; then \
         echo "Expected $expected server catalogs embedded in Maki.Api.dll, found $actual." >&2; \
         exit 1; \
       fi

# ---- libjxl ----
# The upscaler writes colour pages as JPEG XL through libjxl, which neither the bundled libvips nor
# Ubuntu 24.04 (0.7, from 2022) provides in a usable version. Built on $BUILDPLATFORM and
# cross-compiled for arm64: compiling it under qemu takes far longer. Nothing here depends on the
# app's source, so the layer stays cached until build.sh changes.
FROM --platform=$BUILDPLATFORM ubuntu:24.04 AS libjxl
ARG TARGETARCH
ARG BUILDARCH
RUN apt-get update \
    && case "$TARGETARCH" in \
         amd64|arm64) ;; \
         *) echo "unknown TARGETARCH: $TARGETARCH" >&2; exit 1 ;; \
       esac \
    && if [ "$TARGETARCH" = "$BUILDARCH" ]; then compiler=g++; \
       elif [ "$TARGETARCH" = arm64 ]; then compiler=g++-aarch64-linux-gnu; \
       else compiler=g++-x86-64-linux-gnu; fi \
    && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends git ca-certificates cmake ninja-build binutils "$compiler" \
    && rm -rf /var/lib/apt/lists/*
COPY distribution/libjxl/build.sh /libjxl/build.sh
RUN if [ "$TARGETARCH" = "$BUILDARCH" ]; then \
      bash /libjxl/build.sh /out; \
    elif [ "$TARGETARCH" = arm64 ]; then \
      bash /libjxl/build.sh /out -DCMAKE_SYSTEM_NAME=Linux -DCMAKE_SYSTEM_PROCESSOR=aarch64 \
        -DCMAKE_C_COMPILER=aarch64-linux-gnu-gcc -DCMAKE_CXX_COMPILER=aarch64-linux-gnu-g++; \
    else \
      bash /libjxl/build.sh /out -DCMAKE_SYSTEM_NAME=Linux -DCMAKE_SYSTEM_PROCESSOR=x86_64 \
        -DCMAKE_C_COMPILER=x86_64-linux-gnu-gcc -DCMAKE_CXX_COMPILER=x86_64-linux-gnu-g++; \
    fi

# ---- Headless browser ----
# MangaFire's vrf request signature and TopManhua's image CDN only answer a real browser. Maki drives
# Chrome Headless Shell over the DevTools protocol from its own process, so all the image needs is the
# browser and its libraries. amd64 gets the Chrome for Testing build; Chrome for Testing has no Linux
# arm64 build, so arm64 gets Playwright's build of the same Chromium. It lands where
# BrowserInstaller looks (PLAYWRIGHT_BROWSERS_PATH below, the folder named by revision, the
# INSTALLATION_COMPLETE marker), so the app finds it and never downloads one. Nothing here depends on
# the app's source, so the stage stays cached until these pins move; keep them in step with
# BrowserInstaller.Version, BrowserInstaller.Revision and the checksums in BrowserInstaller.Build
# (BrowserInstallerTests checks). Only the en-US locale pack is kept, since LANG is unset and
# Chromium never loads another.
FROM --platform=$BUILDPLATFORM alpine:3.24 AS browser
ARG TARGETARCH
ARG BROWSER_VERSION=149.0.7827.55
ARG BROWSER_REVISION=1228
ARG BROWSER_SHA256_AMD64=410c9407d5de3fea80d9398666be06f2aa09154a3fa7b327dc254e336bb4c4b7
ARG BROWSER_SHA256_ARM64=1652929a70f4afb17aca36fce073fb7ed22262d16825be761b0801972f43ac4f
RUN apk add --no-cache curl unzip \
    && case "$TARGETARCH" in \
         amd64) url="https://cdn.playwright.dev/builds/cft/$BROWSER_VERSION/linux64/chrome-headless-shell-linux64.zip" \
                exe=chrome-headless-shell-linux64/chrome-headless-shell sha="$BROWSER_SHA256_AMD64" ;; \
         arm64) url="https://cdn.playwright.dev/dbazure/download/playwright/builds/chromium/$BROWSER_REVISION/chromium-headless-shell-linux-arm64.zip" \
                exe=chrome-linux/headless_shell sha="$BROWSER_SHA256_ARM64" ;; \
         *) echo "unknown TARGETARCH: $TARGETARCH" >&2; exit 1 ;; \
       esac \
    && dir=/ms-playwright/chromium_headless_shell-$BROWSER_REVISION \
    && mkdir -p "$dir" \
    && curl -fsSL --retry 3 -o /tmp/shell.zip "$url" \
    && echo "$sha  /tmp/shell.zip" | sha256sum -c - \
    && unzip -q /tmp/shell.zip -d "$dir" \
    && rm /tmp/shell.zip \
    && find "$dir" -path '*/locales/*.pak' ! -name 'en-US.pak' -delete \
    && chmod -R a+rX /ms-playwright \
    && chmod a+rx "$dir/$exe" \
    && touch "$dir/INSTALLATION_COMPLETE"

# ---- Runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0

# tini runs as PID 1, reaps orphans and forwards signals to the entrypoint. A browser Maki starts
# itself left no zombies with dotnet as PID 1, but one started by a `docker exec` command (a
# --selftest, a troubleshooting tool) left two per launch (measured 2026-11-19): its helpers
# outlive their parent and fall to PID 1, which did not reap them.
RUN apt-get update \
    && apt-get install -y --no-install-recommends gosu curl tini \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
# The headless browser's shared libraries and fonts, the set Playwright's `install --with-deps`
# used to apt-install (less xvfb, which only a headed browser uses). Before any app file, so a code
# change never reruns it. PLAYWRIGHT_BROWSERS_PATH is a shared, world-readable path so the app (run
# via gosu as PUID) finds the browser; the name is Playwright's, kept so the folder layout an older
# image wrote still reads the same.
COPY distribution/native/install-browser-deps.sh /tmp/install-browser-deps.sh
RUN sh /tmp/install-browser-deps.sh \
    && rm -rf /var/lib/apt/lists/* /tmp/install-browser-deps.sh
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
COPY --from=browser /ms-playwright /ms-playwright
# Every shared library the shell links has to resolve, or the image builds and the browser never starts.
RUN exe=$(find /ms-playwright -type f \( -name chrome-headless-shell -o -name headless_shell \)) \
    && if ldd "$exe" | grep "not found"; then exit 1; fi

COPY distribution/docker/entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh
COPY --from=backend /app/publish ./
# Flat beside Maki.Api.dll, where JpegXl looks; the libraries find each other through an $ORIGIN rpath.
COPY --from=libjxl /out/ ./
COPY THIRD_PARTY_NOTICES.md ./
COPY --from=frontend /src/frontend/dist ./wwwroot/

# The runtime maps JIT-generated code twice, so a page is never both writable and executable: the
# executable view is what runs, a separate writable view is what the JIT writes through. It is
# defence in depth against an attacker who can already write to memory in this process turning that
# into running their own code. Both views count toward RSS, and on a NAS that is 77 MB (measured
# inside the container: RssShmem went from 77 MB to zero with this set) held for a mitigation
# against an attack that starts from an arbitrary-write primitive Maki does not hand anyone.
#
# Worth 77 MB on hardware where 8 GB is common, which is most of what runs this. A machine with RAM
# to spare should make the opposite call: -e DOTNET_EnableWriteXorExecute=1 takes the hardening back.
ENV DOTNET_EnableWriteXorExecute=0

# glibc gives each thread that mallocs its own arena, up to eight per core, and each grows to 64 MB
# and is trimmed only from its own top. A container on a many-core NAS therefore accumulates native
# heap in proportion to the host's core count rather than to anything the app is doing - and this
# app has native allocators that run on the thread pool (the image decoders during a scan, SQLite
# on every query). Two arenas cost some contention when many threads allocate natively at once and
# stop the count scaling with a machine Maki has no other reason to care about the size of.
ENV MALLOC_ARENA_MAX=2

# glibc raises its mmap threshold every time a large mapped block is freed, up to 32 MB, so after
# the first ONNX model parse or libvips decode every big buffer lands in an arena instead of its own
# mapping, and arenas only shrink from the top. That freed-but-kept heap was 100 to 165 MB of what
# a container still held ten minutes after search and Discover had unloaded. Fixing both thresholds
# at 128 KB sends large allocations to mmap, which free unmaps at once: 411 and 428 MB after unload
# against 547 to 694 MB without, with no CPU difference in the warm-up. The mmap threshold is the
# part that matters; a trim threshold alone saved 26 MB.
ENV MALLOC_MMAP_THRESHOLD_=131072
ENV MALLOC_TRIM_THRESHOLD_=131072

# The runtime loads its LTTng tracing provider in every process, which maps liblttng-ust and starts
# two idle dotnet-ust threads (about 1 MB). Nothing in this image uses LTTng; EventPipe tools
# (dotnet-trace, dotnet-counters, dotnet-gcdump) still work without it.
ENV DOTNET_LTTng=0

ENV MAKI_CONFIG_DIR=/config
ENV MAKI_RUNTIME=docker
VOLUME /config
EXPOSE 8990

# The port is configurable in config.json, so read it from there and fall back to the default.
HEALTHCHECK --interval=60s --timeout=10s --start-period=30s \
    CMD p=$(grep -ioE '"port"[[:space:]]*:[[:space:]]*[0-9]+' /config/config.json 2>/dev/null | grep -oE '[0-9]+$'); \
        curl -f "http://localhost:${p:-8990}/initialize.json" || exit 1

# Last, so a new version only changes this metadata layer; see ARG VERSION in the backend stage.
ARG VERSION
ARG SOURCE_COMMIT
LABEL org.opencontainers.image.title="Maki" \
      org.opencontainers.image.description="Manga collection manager for the *arr ecosystem" \
      org.opencontainers.image.source="https://github.com/Orbit/Maki" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${SOURCE_COMMIT}"

# With `init: true` in a compose file docker-init is PID 1 instead; TINI_SUBREAPER keeps tini
# reaping as its child and stops it warning that it is not PID 1.
ENV TINI_SUBREAPER=1
ENTRYPOINT ["/usr/bin/tini", "--", "/entrypoint.sh"]
