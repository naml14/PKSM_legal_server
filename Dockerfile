# build-go image
FROM golang:latest as build-go
WORKDIR /build

RUN apt-get update && apt-get install --assume-yes upx
COPY . /build
RUN make go-build
RUN upx --best --lzma coreapi

# build-cs-release image
# The engine targets net10.0 (PKHeX.Core / AutoMod 26.10.18), so it needs the
# .NET 10 SDK.
FROM mcr.microsoft.com/dotnet/sdk:10.0 as build-cs-release
WORKDIR /build

COPY . /build
RUN apt-get update && apt-get install --assume-yes make
RUN make cs-build-release


# Run image
# The Go binary is static (CGO_ENABLED=0) and the engine only needs the
# .NET 10 runtime, the Mono runtime of the old engine is not used anymore.
FROM mcr.microsoft.com/dotnet/runtime:10.0

RUN mkdir /app
RUN mkdir /data
RUN mkdir /app/css
COPY --from=build-go /build/coreapi /app
COPY --from=build-go /build/start.sh /app
COPY --from=build-go /build/.env.example /data/.env
# /app/cc holds the engine binary and its data folder (the sprites).
COPY --from=build-cs-release /build/cc /app/cc

# runtime params
WORKDIR /app
ENV PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin

EXPOSE 8080

CMD ["/bin/sh", "start.sh"]
