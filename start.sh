#!/bin/sh
# Entrypoint of the container.
#
# The API reads its configuration from the environment. The engine reads it
# from a .env file in ITS OWN working directory (./cc) and exits with code 4
# without it, which the API answers as a 503.
#
# Two ways to provide that file:
#   - a .env file mounted at /data/.env (or at ENV_FILE): it is copied to both
#     places;
#   - environment variables only (Coolify, docker compose): the file the engine
#     needs is written from them.
set -e

app_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
env_file="${ENV_FILE:-/data/.env}"

mkdir -p "$app_dir/cc"

if [ -f "$env_file" ]; then
	# The API reads the .env from its own working directory.
	cp "$env_file" "$app_dir/.env"
	cp "$env_file" "$app_dir/cc/.env"
else
	# No file: the configuration comes from the environment. The engine only
	# needs SENTRY_DSN; the API reads the variables directly.
	umask 077
	printf 'SENTRY_DSN=%s\n' "${SENTRY_DSN:-}" > "$app_dir/.env"
	cp "$app_dir/.env" "$app_dir/cc/.env"
fi

cd "$app_dir"
exec ./coreapi
