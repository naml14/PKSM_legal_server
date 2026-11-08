# ---------------------------------------------------------------------------
# Production image for the VPS (Coolify, docker compose).
#
# THE KEYS DO NOT GO IN HERE.
# The session and encryption keys and the Sentry DSNs arrive at runtime:
#   - Coolify: Application > Environment Variables
#   - docker compose: from the .env next to this file (chmod 600; it is in
#     .gitignore and .dockerignore, so it never enters the image)
#
# A key written in the Dockerfile stays in the image layers and can be read
# with `docker history`.
#
# See docker-compose.yml for the full setup.
# ---------------------------------------------------------------------------

# ─── Go API ──────────────────────────────────────────────────────────────────
FROM golang:latest AS build-go
WORKDIR /build

RUN apt-get update && apt-get install --assume-yes upx
COPY . /build
RUN make go-build
RUN upx --best --lzma coreapi

# ─── Legality engine (.NET 10) ───────────────────────────────────────────────
# The engine targets net10.0 (PKHeX.Core / AutoMod 26.10.18), so it needs the
# .NET 10 SDK.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build-cs-release
WORKDIR /build

COPY . /build
RUN apt-get update && apt-get install --assume-yes make
RUN make cs-build-release

# ─── Runtime ─────────────────────────────────────────────────────────────────
# The Go binary is static (CGO_ENABLED=0) and the engine only needs the .NET 10
# runtime; the Mono runtime of the old engine is not used any more.
FROM mcr.microsoft.com/dotnet/runtime:10.0

# curl is only there for the healthcheck below.
RUN apt-get update && apt-get install --assume-yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

RUN mkdir /app /data
COPY --from=build-go /build/coreapi /app
COPY --from=build-go /build/start.sh /app
# /app/cc holds the engine binary and its data folder (the sprites).
COPY --from=build-cs-release /build/cc /app/cc

# There is no .env in the image: the configuration arrives either as a file
# mounted at /data/.env or as environment variables, and start.sh writes the
# file the engine needs from whichever of the two it gets.

WORKDIR /app
ENV PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin

EXPOSE 8080

# The endpoint the proxy and Coolify use to know the service is alive.
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl -fsS http://127.0.0.1:8080/api/general/kuma-ping || exit 1

CMD ["/bin/sh", "start.sh"]
