#!/bin/sh
# Entrypoint of the container.
set -e

app_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
env_file="${ENV_FILE:-/data/.env}"

if [ ! -f "$env_file" ]; then
	env_file="$app_dir/.env"
fi

if [ ! -f "$env_file" ]; then
	echo "no .env file found (looked at /data/.env and $app_dir/.env)" >&2
	exit 1
fi

# The Go API reads the .env from its own working directory.
cp "$env_file" "$app_dir/.env"

# The engine reads the .env from ITS working directory (./cc): without it
# coreconsole exits with code 4 and the API answers 503.
mkdir -p "$app_dir/cc"
cp "$env_file" "$app_dir/cc/.env"

cd "$app_dir"
exec ./coreapi
