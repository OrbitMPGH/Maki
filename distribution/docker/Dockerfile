# ---- Frontend build ----
FROM node:22-alpine AS frontend
WORKDIR /src/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

# ---- Backend build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
WORKDIR /src
COPY Directory.Build.props ./
COPY Mangarr.sln ./
COPY src/ src/
COPY tests/ tests/

# .dockerignore excludes .git, so the version cannot be derived from a tag in here — CI computes it
# from the ref and passes it down. A plain `docker build` gets the -dev default from
# Directory.Build.props, which is the intended tell for an unofficial image.
ARG VERSION
ARG SOURCE_COMMIT

RUN dotnet restore src/Mangarr.Api/Mangarr.Api.csproj
RUN dotnet publish src/Mangarr.Api/Mangarr.Api.csproj -c Release -o /app/publish /p:UseAppHost=false \
      ${VERSION:+/p:Version=$VERSION} \
      ${VERSION:+/p:InformationalVersion=$VERSION${SOURCE_COMMIT:++$SOURCE_COMMIT}}

# ---- Runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG VERSION
ARG SOURCE_COMMIT
LABEL org.opencontainers.image.title="Mangarr" \
      org.opencontainers.image.description="Manga collection manager for the *arr ecosystem" \
      org.opencontainers.image.source="https://github.com/Orbit/Mangarr" \
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

ENV MANGARR_CONFIG_DIR=/config
VOLUME /config
EXPOSE 8990

HEALTHCHECK --interval=60s --timeout=10s --start-period=30s \
    CMD curl -f http://localhost:8990/initialize.json || exit 1

ENTRYPOINT ["/entrypoint.sh"]
