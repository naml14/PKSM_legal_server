.DEFAULT_GOAL := build-all

export PROJECT := "coreapi"
export PACKAGE := "github.com/FlagBrew/CoreAPI"

VERSION=$(shell git describe --tags --always --abbrev=0 --match=v* 2> /dev/null | sed -r "s:^v::g" || echo 0)
VERSION_FULL=$(shell git describe --tags --always --dirty --match=v* 2> /dev/null | sed -r "s:^v::g" || echo 0)

# The .NET SDK is not always available in the PATH (and the dotnet that is
# there may only carry the runtime), so the SDK shipped in the project
# (.dotnet10/) is used when it is there and the one in the PATH otherwise.
ifeq ($(OS),Windows_NT)
LOCAL_DOTNET := $(wildcard .dotnet10/dotnet.exe)
else
LOCAL_DOTNET := $(wildcard .dotnet10/dotnet)
endif

ifeq ($(strip $(LOCAL_DOTNET)),)
DOTNET := dotnet
else
DOTNET := $(CURDIR)/$(LOCAL_DOTNET)
export DOTNET_ROOT := $(CURDIR)/.dotnet10
endif

# The NuGet.Config of the machine may not define any source at all, and then
# every restore fails with NU1100, so the source is given explicitly here.
NUGET_SOURCE ?= https://api.nuget.org/v3/index.json

# The static linker flags only exist on the platforms that can link
# statically, so they are left out on Windows.
ifeq ($(OS),Windows_NT)
GO_LDFLAGS := -d -s -w
GO_BINARY := coreapi.exe
else
GO_LDFLAGS := -d -s -w -extldflags=-static
GO_BINARY := coreapi
endif

build-all: cs-build-release go-build

# General
clean: cs-clean
	/bin/rm -rfv ${PROJECT} coreapi.exe



# Docker
docker:
	docker compose \
		--project-name ${COMPOSE_PROJECT} \
		--file docker-compose.yml \
		up \
		--remove-orphans \
		--build \
		--timeout 0 ${COMPOSE_ARGS}

docker-clean:
	docker compose \
		--project-name ${COMPOSE_PROJECT} \
		--file docker-compose.yml \
		down \
		--volumes \
		--remove-orphans \
		--rmi local --timeout 1

docker-build:
	docker build \
		--tag ${PROJECT} \
		--force-rm .


# CSharp
# The engine targets net10.0 (PKHeX.Core / AutoMod 26.10.18), so it needs the
# .NET 10 SDK.

cs-build-release:
	cd coreconsole && \
	$(DOTNET) build coreconsole.csproj -c Release -o ../cc -p:RestoreSources=$(NUGET_SOURCE) && \
	mkdir -p ../cc/data && \
	cp -r data/. ../cc/data/


cs-build-debug:
	cd coreconsole && \
	$(DOTNET) build coreconsole.csproj -c Debug -o ../cc -p:RestoreSources=$(NUGET_SOURCE) && \
	mkdir -p ../cc/data && \
	cp -r data/. ../cc/data/

cs-test:
	$(DOTNET) test coreconsole/Tests/Tests.csproj -p:RestoreSources=$(NUGET_SOURCE)

cs-clean:
	/bin/rm -rfv cc

# Go
go-fetch:
	go mod download
	go mod tidy

go-upgrade-deps:
	go get -u ./...
	go mod tidy

go-upgrade-deps-patch:
	go get -u=patch ./...
	go mod tidy

go-dlv: go-fetch
	dlv debug \
		--headless --listen=:2345 \
		--api-version=2 --log \
		--allow-non-terminal-interactive \
		${PACKAGE} -- --debug

go-debug: go-fetch
	go run ${PACKAGE} --debug

go-debug-fast:
	go run ${PACKAGE} --debug

go-test:
	go test ./...

go-build: go-fetch
	CGO_ENABLED=0 \
	go build \
		-ldflags '$(GO_LDFLAGS)' \
		-tags=netgo,osusergo,static_build \
		-installsuffix netgo \
		-buildvcs=false \
		-trimpath \
		-o $(GO_BINARY) \
		${PACKAGE}

test: go-test cs-test
