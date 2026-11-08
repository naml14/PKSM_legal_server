# CoreAPI

CoreAPI is the legality service behind the PKSM app for 2ds/3ds. It is made of
two parts:

- a **Go API** (`main.go`, `http.go`) that exposes the HTTP endpoints,
- a **C# engine** (`coreconsole/`) that does the actual legality work with
  PKHeX.Core and Auto Legality Mod, driven by the API through the command line.

The engine targets **.NET 10** and uses **PKHeX.Core / AutoMod 26.10.18**.

## Requirements

- **Go 1.20+** (the version declared in `go.mod`).
- **A .NET 10 SDK**, either in the `PATH` or in the project itself, in
  `.dotnet10/` (the Makefile picks the local one up automatically when it is
  there).

## Configuration

The configuration lives in a `.env` file, and it is read from **two** places:

| File | Read by |
| --- | --- |
| `./.env` | the Go API, from its own working directory |
| `./cc/.env` | the C# engine, from **its** working directory (`./cc`) |

Without `./cc/.env` the engine exits with code 4 and the API answers `503`.

Start from the template and change the values (the `cc/` folder is created by
`make cs-build-release`):

```bash
make
cp .env.example .env
cp .env.example cc/.env
```

The keys the API reads are the ones declared in `internal/models/flags.go`:

| Key | Meaning |
| --- | --- |
| `CONFIGURED` | has to be `true`, otherwise the app exits with "Not configured yet" |
| `ENV` | `production` or `development` |
| `HTTP_ADDR` | ip:port the server binds to (`:8080` by default) |
| `HTTP_BASE_URL` | public base URL of the API |
| `HTTP_SESSION_KEY` | salted session key (change it to log every user out) |
| `HTTP_TRUSTED_PROXIES` | CIDR range allowed to send `X-Forwarded-For` (one value, no comma separated list) |
| `HTTP_VALIDATION_KEY` | key used to validate the session cookies (32 or 64 bytes) |
| `HTTP_ENCRYPTION_KEY` | key used to encrypt the session cookies (32 bytes) |
| `LOGGING_SENTRY_DSN` | Sentry DSN used by the Go API |
| `SENTRY_DSN` | Sentry DSN used by the C# engine |

## Build

```bash
make            # cs-build-release + go-build
```

| Target | What it does |
| --- | --- |
| `make cs-build-release` | builds the engine into `cc/` (and copies the sprites to `cc/data`) |
| `make cs-build-debug` | same, in debug |
| `make go-build` | builds the API binary (`coreapi`, `coreapi.exe` on Windows) |
| `make go-test` | `go test ./...` |
| `make cs-test` | `dotnet test coreconsole/Tests/Tests.csproj` |
| `make test` | both test suites |
| `make clean` | removes the binaries and `cc/` |

The .NET restore uses nuget.org explicitly (`NUGET_SOURCE`, overridable),
because a machine `NuGet.Config` without any source makes the restore fail
with `NU1100`.

## Run it locally

```bash
make                          # builds cc/ (the engine) and the coreapi binary
cp .env.example .env          # the API reads it from its working directory
cp .env.example cc/.env       # the engine reads it from its own directory
./coreapi            # ./coreapi.exe on Windows
```

The API then listens on `http://localhost:8080`.

With the local SDK in `.dotnet10/` the engine can also be run by hand:

```bash
cd cc
DOTNET_ROOT=../.dotnet10 ../.dotnet10/dotnet coreconsole.dll version
```

## Run the engine by hand

The engine takes the Pokemon in base64 as its only argument and reads its
`.env` from its working directory:

```bash
cd cc

# the versions of the engine
dotnet coreconsole.dll version

# summary of a Pokemon
dotnet coreconsole.dll summary <base64> [--generation Gen9]

# legality report
dotnet coreconsole.dll legality <base64> [--generation Gen9]

# auto-legalization
dotnet coreconsole.dll legalize <base64> \
  [--generation Gen9] [--legalization-generation Gen9] [--version SV]
```

The build also drops an apphost next to it (`coreconsole` on Linux,
`coreconsole.exe` on Windows) that can be run directly, but it needs the .NET
runtime to be found (`DOTNET_ROOT` when the SDK is the local `.dotnet10/`).

- `--generation` tells the engine the generation of the file, needed when the
  format could be 6/7, 8/8b or 9/9a. It accepts the names (`Gen1` .. `Gen9`,
  `Gen7b`, `Gen8a`, `Gen8b`) and the plain numbers (`1` .. `9`).
- `--legalization-generation` forces the generation the legalized Pokemon has
  to belong to.
- `--version` is the game version used while legalizing (`SV`, `ORAS`, ...).

The modes are `version`, `summary`, `legality` and `legalize`.

## Endpoints

| Method | Route | What it does |
| --- | --- | --- |
| `GET` | `/api/general/ping` | Runs the engine and returns the ALM/PKHeX versions and the response time. Answers `503` while the engine is not set up |
| `GET` | `/api/general/kuma-ping` | Liveness probe, answers `{"status":"ok"}` |
| `POST` | `/api/info` | Summary of the uploaded Pokemon: species, sprites, legality, illegal reasons and the base64 Pokemon. Multipart field `pkmn`, optional `generation` |
| `POST` | `/api/legality/check` | Legality report of the uploaded Pokemon (`legal` plus the list of reasons) |
| `POST` | `/api/legality/legalize` | Tries to auto-legalize the Pokemon and returns the new Pokemon, the report and the list of changes |
| `POST` | `/api/v2/pksm/legality` | v2 legality check, the one the PKSM app for 2ds/3ds uses |
| `POST` | `/api/v2/pksm/legalize` | v2 auto-legalization, the one the PKSM app for 2ds/3ds uses |

The v2 routes take the Pokemon as the multipart field `pkmn` and read the
generation from the `generation` header (or query parameter): `Gen1` .. `Gen9`,
`Gen7b`, `Gen8a`, `Gen8b` or the plain numbers. `/api/v2/pksm/legalize` also
needs the `version` header with the game version (`SV`, `ORAS`, ...).

`GET /.well-known/security.txt` is served too, and `/debug/pprof/` when the
API is started with `--debug`.

## Verification tools

The `tools/` folder has the scripts used to check a running API:

| Script | What it does |
| --- | --- |
| `bash tools/verify-endpoints.sh [base_url]` | Checks the endpoint contract: routes, methods, error codes and the verdict of every fixture |
| `python tools/verify-legalization.py [base_url]` | Full auto-legalization run: the Pokemon returned by the API has to pass the legality check with **no reasons left** |
| `python tools/update-baseline.py [base_url] [--write]` | Regenerates `tools/verify-baseline.txt`; it prints the changes and only rewrites the file with `--write` |

The test fixtures (the real Pokemon files used by those scripts) are in
`pokemons/`.

## Deployment

### Docker

```bash
make docker-build

# the configuration is read from /data/.env inside the container
mkdir -p dockerdata && cp .env.example dockerdata/.env
docker run --rm -p 8080:8080 -v "$(pwd)/dockerdata:/data" pksm-legal-server
```

The image is built in three stages: the Go binary, the C# engine (with the
.NET 10 SDK) and a runtime image with the .NET 10 runtime. `start.sh` copies
`/data/.env` to `/app/.env` for the API and to `/app/cc/.env` for the engine,
and then runs the API as the main process.

### Coolify (VPS)

`docker-compose.yml` is the deployment descriptor, with the same Traefik
pattern as the mail server that is already running:

- **Coolify** → Projects → New Resource → Docker Compose Empty → paste
  `docker-compose.yml` → Environment Variables → the keys listed below.
- The hostname in the Traefik label goes **literal**: Coolify does not
  interpolate variables inside the labels, so `${DOMAIN}` there makes Traefik
  reject the rule and it never asks for a certificate. Change the line in
  `docker-compose.yml` (and the DNS record) to change the domain.
- The service listens on **8080** and the health endpoint is
  `/api/general/kuma-ping`; the compose file declares the healthcheck that
  Coolify and the proxy use.
- The keys are **never** written in the compose file, the Dockerfile or the
  repository: they arrive as environment variables. A key written in the
  Dockerfile stays in the image layers and can be read with `docker history`.

#### The configuration

The API reads its configuration from the environment, and the engine reads it
from a `.env` file in its own working directory. `start.sh` covers both
cases: a `.env` file mounted at `/data/.env` is copied to the two places
that need it, and when there is no file (the usual Coolify case, where the
values come as environment variables) the file the engine needs is written
from the environment.

Every one of these variables has to be **present**, an empty value is enough
(the API refuses to start with "the required flag ... was not specified"):

| Variable | Value |
|---|---|
| `CONFIGURED` | `true` |
| `ENV` | `production` |
| `HTTP_ADDR` | `:8080` |
| `HTTP_BASE_URL` | the public URL of the service, `https://<domain>` |
| `HTTP_VALIDATION_KEY` | 32 or 64 bytes |
| `HTTP_ENCRYPTION_KEY` | 32 bytes |
| `HTTP_SESSION_KEY` | any random string |
| `HTTP_TRUSTED_PROXIES` | the CIDR of the reverse proxy (`172.16.0.0/12` for the default Docker network), so the client IP is read from `X-Forwarded-For` |
| `LOGGING_SENTRY_DSN` | the DSN of the API, may be empty |
| `SENTRY_DSN` | the DSN of the engine, may be empty |

Without `HTTP_TRUSTED_PROXIES` every request looks like it comes from the
proxy, so they all share the rate limit (100 requests per minute and IP).

### The PKSM app (2ds/3ds)

The app asks the service through the `Local API URL` setting. Two things
about it:

- The field takes **29 characters at most**: the on-screen keyboard of the
  3DS is 29 columns wide.
- The app concatenates the base with the path (`base + "api/v2/pksm/legality"`),
  so the base **needs the trailing slash**.

`https://electrondevnaml.dev/` is 28 characters and satisfies both. The
longer `https://legal.electrondevnaml.dev/` works for anything else (a
browser, a script), but it does not fit in that field.

HTTPS works: the client of the app turns certificate verification off on
purpose, because the 3DS has no certificate store to check a chain
against.

## Origin, license and attribution

This repository continues **FlagBrew/CoreAPI**
(<https://github.com/FlagBrew/CoreAPI>), the API server that PKSM uses for
auto legality and GPSS. That project was abandoned (its last commit is
2023-10-11) and it never published a license; its history is kept intact
here, so `git log upstream/master` shows where every change comes from.

Everything added here is original work on top of that baseline: the routes
the PKSM app for 2ds/3ds needs, the legality engine on PKHeX/ALM 26.10.18,
and the automatic legalization driven by what the legality check reports.

### License

The code is available under **either the MIT license or the GNU General Public
License v3.0, at your option**:

- [`licenses/LICENSE-MIT.txt`](licenses/LICENSE-MIT.txt)
- [`licenses/LICENSE-GPL-3.0.txt`](licenses/LICENSE-GPL-3.0.txt)

### Attribution is required

Whichever license you pick, **if you use this repository you have to name
where you took it from**: keep the [`NOTICE`](NOTICE) file in the copies you
distribute and mention both this project (PKSM_legal_server) and the upstream
project it continues (FlagBrew/CoreAPI) in your README, your release notes or
your documentation.

No license is inherited from the upstream, because it never had one.
