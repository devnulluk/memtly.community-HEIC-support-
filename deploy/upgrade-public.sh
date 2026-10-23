#!/usr/bin/env bash
# Upgrade the existing wedding deployment; preserve local Compose, secrets and media.
set -euo pipefail
umask 077
cd "${MEMTLY_DIR:-/opt/memtly-wedding}"
BRANCH=feature/wedding-guest-mode
MOUNT=/mnt/HC_Volume_107249853
EXPECTED=e4ff5be9-af62-4e30-8793-4a483eebc8d4
IMAGE=newland-brown-memtly:wedding
URL=https://wedding-photos.newland-brown.com
COMPOSE=(docker compose -f compose.wedding.yml)
[[ $EUID -eq 0 ]] || { echo 'Run this deployment script as root.' >&2; exit 1; }
for tool in git docker python3 curl findmnt; do command -v "$tool" >/dev/null; done
[[ $(git branch --show-current) == "$BRANCH" ]] || { echo "Expected branch $BRANCH; no changes made." >&2; exit 1; }
[[ -f .env && -f compose.wedding.yml && -f Dockerfile.wedding ]] || { echo 'Existing configuration is missing.' >&2; exit 1; }
check_storage() {
  [[ $(findmnt -n -o UUID --mountpoint "$MOUNT") == "$EXPECTED" ]] || { echo 'Wedding filesystem is not mounted correctly.' >&2; return 1; }
  [[ -d "$MOUNT/wedding-media" && ! -L "$MOUNT/wedding-media" ]] || return 1
  [[ $(cat "$MOUNT/wedding-media/.volume-id") == "$EXPECTED" ]] || { echo 'Storage marker mismatch.' >&2; return 1; }
}
check_storage
"${COMPOSE[@]}" config --quiet
"${COMPOSE[@]}" config --format json | python3 -c '
import json,sys
c=json.load(sys.stdin); s=c["services"]["memtly"]
assert s["image"] == "newland-brown-memtly:wedding", "Unexpected image name"
m=next(v for v in s["volumes"] if v["target"] == "/data/wedding")
assert m["type"] == "bind" and m["source"] == "/mnt/HC_Volume_107249853/wedding-media", "Wrong media mapping"
assert any(v["target"] == "/app/config" and v["type"] == "volume" for v in s["volumes"]), "Missing persistent configuration volume"
assert s.get("entrypoint"), "Expected the existing storage-marker startup guard"
'
while IFS= read -r changed; do
  case "$changed" in Dockerfile.wedding|compose.wedding.yml|'') ;; *) echo "Unexpected local modification: $changed. Preserving it; upgrade stopped." >&2; exit 1;; esac
done < <(git diff --name-only HEAD)
git fetch origin "$BRANCH:refs/remotes/origin/$BRANCH"
target=$(git rev-parse "origin/$BRANCH")
git merge-base --is-ancestor HEAD "$target" || { echo 'Local branch has diverged; refusing to overwrite it.' >&2; exit 1; }
git diff --quiet HEAD "$target" -- compose.wedding.yml || { echo 'Upstream Compose changed; manual reconciliation is needed to preserve the Hetzner bind mount.' >&2; exit 1; }
cid=$("${COMPOSE[@]}" ps -q memtly)
[[ -n "$cid" ]] || { echo 'Expected the existing running Memtly container.' >&2; exit 1; }
old_image=$(docker inspect --format '{{.Image}}' "$cid")
stamp=$(date -u +%Y%m%dT%H%M%SZ)-$$
backup="/root/memtly-deploy-backups/$stamp"
mkdir -p "$backup"
cp -a .env compose.wedding.yml Dockerfile.wedding "$backup/"
[[ ! -f deploy/Caddyfile ]] || cp -a deploy/Caddyfile "$backup/"
git rev-parse HEAD > "$backup/previous-commit.txt"
git diff --binary HEAD -- Dockerfile.wedding compose.wedding.yml > "$backup/local-changes.patch"
rollback="newland-brown-memtly:before-public-$stamp"
candidate="newland-brown-memtly:public-candidate-$stamp"
docker image tag "$old_image" "$rollback"
printf '%s\n' "$rollback" > "$backup/rollback-image.txt"
cutover=0
source_merged=0
finish() {
  status=$?
  set +e
  if [[ $status -ne 0 ]]; then
    if [[ $source_merged -eq 0 ]]; then cp -a "$backup/Dockerfile.wedding" Dockerfile.wedding; fi
    if [[ $cutover -eq 1 ]]; then
      echo 'Update did not pass verification; restoring the previous application image.' >&2
      docker image tag "$rollback" "$IMAGE"
      "${COMPOSE[@]}" up -d --no-deps --no-build --force-recreate memtly
    fi
    echo "Upgrade stopped. Backups are in $backup; no media volumes were deleted." >&2
  fi
}
trap finish EXIT
# The local webpack fix is now in the tracked Dockerfile. Only that file is reset,
# after making a protected backup. Local Compose and .env are never replaced.
git restore --source=HEAD --staged --worktree -- Dockerfile.wedding
git merge --ff-only "$target"
source_merged=1
git submodule sync --recursive
git submodule update --init --recursive
cmp -s "$backup/compose.wedding.yml" compose.wedding.yml
cmp -s "$backup/.env" .env
# Build under a new tag while the current website remains running.
docker build -f Dockerfile.wedding -t "$candidate" .
docker run --rm --entrypoint sh "$candidate" -ec 'test "$(id -u)" != 0; command -v ffmpeg >/dev/null; test -s /app/wwwroot/wedding/index.html'
check_storage
if [[ -n $(find "$MOUNT/wedding-media" -maxdepth 1 -type d -name '.incoming-*' -print -quit) ]]; then
  echo 'An upload or incomplete staging directory exists. Leaving the running application unchanged; retry during a quiet period.' >&2
  exit 1
fi
cutover=1
"${COMPOSE[@]}" stop -t 150 memtly
# Cold-copy SQLite, WAL files and Data Protection keys consistently.
docker cp "$cid:/app/config" "$backup/config"
docker image tag "$candidate" "$IMAGE"
"${COMPOSE[@]}" up -d --no-deps --no-build --force-recreate memtly
verified=0
for attempt in $(seq 1 45); do
  if curl -fsS --connect-timeout 5 --max-time 15 -o /dev/null "$URL/" && \
     curl -fsS --connect-timeout 5 --max-time 15 -o /dev/null "$URL/Account/Login"; then
    if curl -fsS --connect-timeout 5 --max-time 15 "$URL/Wedding/Session" |
       python3 -c 'import json,sys; d=json.load(sys.stdin); assert d["publicAccess"] is True and d["invited"] is True'; then
      verified=1
      break
    fi
  fi
  sleep 2
done
[[ $verified -eq 1 ]] || { echo 'Public page/login/session checks failed.' >&2; exit 1; }
cutover=0
git rev-parse HEAD > "$backup/deployment-commit.txt"
printf '\nPublic wedding upload page is ready: %s/\n' "$URL"
printf 'Administration: %s/Wedding/Admin\n' "$URL"
printf 'Previous image and protected configuration backup: %s\n' "$backup"
printf 'Caddy, .env, your local Compose storage guard and the media volume were retained.\n'
