#!/usr/bin/env bash
# Checks that the running `web` container was built from the checked-out commit ($GIT_SHA):
#   1. the image's org.opencontainers.image.revision label equals $GIT_SHA;
#   2. the newest EF migration in this checkout is compiled into the app inside the container.
# Used by .github/workflows/docker-ci.yml right after `docker compose up`.
set -euo pipefail
: "${GIT_SHA:?GIT_SHA must be set to the commit being tested}"

container="$(docker compose ps -q web)"
image="$(docker inspect --format '{{.Image}}' "$container")"
revision="$(docker inspect --format '{{ index .Config.Labels "org.opencontainers.image.revision" }}' "$image")"
echo "image revision label: $revision"
echo "checked-out commit:   $GIT_SHA"
if [ "$revision" != "$GIT_SHA" ]; then
  echo "The container does not run this commit's image." >&2
  exit 1
fi

newest="$(ls Migrations/*.cs | grep -vE '\.Designer\.cs$|ModelSnapshot\.cs$' | sort | tail -n 1)"
name="$(basename "$newest" .cs)"
echo "newest migration in this checkout: $name"
if ! docker compose exec -T web grep -q "$name" /app/SmartSchoolAI.dll; then
  echo "The app in the container does not contain migration $name." >&2
  exit 1
fi
echo "OK: the container runs commit $GIT_SHA and includes migration $name."
