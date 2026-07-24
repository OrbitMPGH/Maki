# --platform=$BUILDPLATFORM pins these two build stages to the host's native architecture instead
# of buildx's target platform(s). Both produce platform-independent output (static frontend
# assets; framework-dependent .NET IL, no AOT/RID-specific publish), so building them per-target
# would just run npm ci / dotnet publish twice for identical results — once natively, once again
# under qemu emulation for the second platform. Only the final runtime stage below actually needs
# to differ per architecture (it pulls a per-arch base image and installs native apt packages).
# ---- Frontend build ----
FROM --platform=$BUILDPLATFORM node:22-alpine AS frontend
WORKDIR /src/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

# ---- Backend build ----
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
WORKDIR /src
COPY Directory.Build.props ./
COPY Maki.sln ./
COPY src/ src/
COPY tests/ tests/

# .dockerignore excludes .git, so the version cannot be derived from a tag in here — CI computes it
# from the ref and passes it down. A plain `docker build` gets the -dev default from
# Directory.Build.props, which is the intended tell for an unofficial image.
ARG VERSION
ARG SOURCE_COMMIT

RUN dotnet restore src/Maki.Api/Maki.Api.csproj
# PlaywrightPlatform=all is not a recognized platform keyword in Microsoft.Playwright.targets, so
# it hits that target's fallback branch and copies every node/<platform> driver folder instead of
# just the host's. Needed because this publish runs once on $BUILDPLATFORM (native) but its output
# is copied into both the linux/amd64 and linux/arm64 runtime stages below.
RUN dotnet publish src/Maki.Api/Maki.Api.csproj -c Release -o /app/publish /p:UseAppHost=false /p:PlaywrightPlatform=all \
      ${VERSION:+/p:Version=$VERSION} \
      ${VERSION:+/p:InformationalVersion=$VERSION${SOURCE_COMMIT:++$SOURCE_COMMIT}}

# ---- Runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG VERSION
ARG SOURCE_COMMIT
LABEL org.opencontainers.image.title="Maki" \
      org.opencontainers.image.description="Manga collection manager for the *arr ecosystem" \
      org.opencontainers.image.source="https://github.com/Orbit/Maki" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${SOURCE_COMMIT}"

RUN apt-get update \
    && apt-get install -y --no-install-recommends gosu curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=backend /app/publish ./
COPY --from=frontend /src/frontend/dist ./wwwroot/
COPY distribution/docker/entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

# MangaFire's vrf request signature is only defeatable inside a real browser, so install a browser
# for Playwright. We only ever launch headless, so install the ~100 MB chromium-headless-shell rather
# than full Chromium (which also drags in the ~170 MB headed binary) — MangaFireBrowser launches with
# Channel = "chromium-headless-shell" to match. Done in the per-arch runtime stage (browser binaries
# are architecture-specific) via the Node driver shipped in the publish output — the aspnet image has
# no SDK for `dotnet tool` and no pwsh for playwright.ps1. --with-deps apt-installs the shared
# libraries it needs. PLAYWRIGHT_BROWSERS_PATH is a shared, world-readable path so the app (run via
# gosu as PUID) finds it.
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
RUN NODE_ARCH="$([ "$(uname -m)" = "aarch64" ] && echo linux-arm64 || echo linux-x64)" \
    && /app/.playwright/node/"$NODE_ARCH"/node /app/.playwright/package/cli.js install --with-deps chromium-headless-shell \
    && chmod a+rx /app/.playwright/node/"$NODE_ARCH"/node \
    && chmod -R a+rX /ms-playwright \
    && rm -rf /var/lib/apt/lists/*

ENV MAKI_CONFIG_DIR=/config
ENV MAKI_RUNTIME=docker
VOLUME /config
EXPOSE 8990

HEALTHCHECK --interval=60s --timeout=10s --start-period=30s \
    CMD curl -f http://localhost:8990/initialize.json || exit 1

ENTRYPOINT ["/entrypoint.sh"]
