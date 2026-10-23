# Mark & Keziah — wedding photo postbox

**Deploy `feature/wedding-guest-mode`, not master.** This branch now defaults to **open uploads at the root URL and a shared guest preview gallery**. The complete release behaviour, limits and update procedure are in [WEDDING_PUBLIC_MODE.md](WEDDING_PUBLIC_MODE.md). Earlier invitation-only documentation does not describe the new default.

Guest address: `https://wedding-photos.newland-brown.com/`

Couple's collection: `https://wedding-photos.newland-brown.com/Wedding/Admin`

No guest account, code or invitation key is needed. Guests see shared viewing copies; original downloads, ZIP export, hiding items and upload/gallery switches require an administrator account. Anyone who discovers the hostname can participate. The household-only sharing wording is a request, not a mechanism restricting visitors to event attendees.

## Existing Hetzner installation: use the protected updater

The running installation at `/opt/memtly-wedding` has a local Compose modification: `/mnt/HC_Volume_107249853/wedding-media` is bound to `/data/wedding`, with a marker-checking entrypoint. Do not replace that file with the repository's generic Compose file. The updater handles the earlier local webpack Dockerfile fix and preserves Compose and `.env` byte-for-byte.

Run during a quiet period, as root on the VPS:

```bash
cd /opt/memtly-wedding
(
  set -e
  git fetch origin feature/wedding-guest-mode
  script=$(mktemp)
  trap 'rm -f "$script"' EXIT
  git show origin/feature/wedding-guest-mode:deploy/upgrade-public.sh > "$script"
  bash "$script"
)
```

The updater verifies the expected mounted UUID and marker, preserves a rollback image, backs up configuration/secrets, builds while the current site stays running, and recreates **only Memtly**. It checks the public homepage, login page and anonymous session. Verification failure triggers an attempt to restore the old image. Caddy and other Docker workloads are not restarted. The config backup is not a full media backup; existing originals stay on the Hetzner volume. Details and limitations are in [the release guide](WEDDING_PUBLIC_MODE.md).

Do not run `deploy/setup.sh` again on an existing installation. Do not rotate secrets, remove the configuration volume, use `docker compose down -v`, or replace the existing media mapping. Those operations are not necessary for this upgrade.

## Fresh installation on another host

Use Linux with Docker, its Compose plugin, Git and OpenSSL. Run a single Memtly application instance. Start with at least 4 GB RAM and sufficient disk space, then measure real usage; the application has a 3 GB container memory limit.

Choose a real hostname with direct DNS to the VPS. The supplied Caddy needs TCP 80/443 for HTTPS and certificate validation; UDP443 is optional HTTP/3. Do not pass large uploads through a CDN/tunnel with a lower body-size ceiling. Do not expose the application's port5000 directly. The Docker subnet `172.29.147.0/24` must not overlap other networks. An existing reverse proxy can replace Caddy, but its exact IP must be configured in `Wedding__TrustedProxy` and its body-size/timeout limits must permit large uploads.

```bash
git clone --branch feature/wedding-guest-mode --recurse-submodules \
  https://github.com/devnulluk/memtly.community-HEIC-support-.git memtly-wedding
cd memtly-wedding
./deploy/setup.sh wedding-photos.newland-brown.com wedding@newland-brown.com
# Review media storage before deploying: the generic file uses a named volume.
docker compose -f compose.wedding.yml config --quiet
docker compose -f compose.wedding.yml build --pull
docker compose -f compose.wedding.yml up -d
```

Setup creates random credentials and a mode600 `.env`, refusing to overwrite one. The initial username is `admin`, not the email address. Save the generated password privately. The historical setup script may print an invitation-fragment URL: in the new public mode use the plain root URL for guests and QR codes instead. Keep the generated invitation key in `.env` for backwards-compatible cookie identity; it is no longer needed by guests.

Caddy explicitly uses Let's Encrypt and handles certificates, renewal and HTTP redirects. Its data volume must persist. No live DNS, server, certificate or ntfy account is changed by publishing this repository.

## Private originals and shared previews

The application archives received bytes without resizing or rewriting originals. HEIC/HEIF viewing copies are WebP. Public video viewing copies are MP4, up to the first120 seconds, at most1280 pixels/30fps; the complete original is retained. Unsupported media or failed previews never cause the public endpoint to expose the original. This does not claim HDR preview fidelity, Live Photo pairing or support for every HEIF variant.

The browser can convert an asset before sending it. Use an exported unmodified original from Files for an integrity check. The server cannot preserve bytes it never receives.

The wedding collection remains separate from standard Memtly galleries. After signing in, open `/Wedding/Admin`; the ordinary `/Account` dashboard is not the wedding collection. Existing administrator accounts, private Data Protection keys, upload manifests and originals are retained during upgrade.

## Limits and notifications

Byte/count quotas default to zero (no fixed application quota). Positive MAX_* values in existing configuration still apply. The default free-space reserve is10 GiB, with four concurrent uploads globally and one per browser. Public request limits, CSRF/origin checks, file validation, declared-size checking and a close-uploads switch supplement that reserve. These safeguards are not a DDoS service or a hard filesystem quota.

Uploads are streamed. Interrupted files restart from the beginning; this is not cross-browser-restart resumable uploading. Keep the page open and screen awake on a stable connection. Do not upgrade while a long upload is active.

ntfy continues to notify after each committed file, independent of preview generation. Pending notifications survive restarts and retry after failures. Delivery is at-least-once, so lost acknowledgements can produce duplicates. Keep the topic private; optional names/filenames can appear in notifications. The application never attaches the original, invitation key or guest cookie. Settings are documented in [WEDDING_UPLOADS_AND_NTFY.md](WEDDING_UPLOADS_AND_NTFY.md).

## Backup and recovery

Protect all of: the media directory (originals/manifests/previews/public settings), Memtly's config volume (SQLite database and Data Protection keys), `.env`, the local Compose/Caddy files and the deployment commit. Keep an off-server copy. Caddy's data/config volumes hold its certificate state. The updater's local configuration backup does not replace media replication or independent backups.

The generic named volumes are `newland-brown-wedding_memtly_config`, `newland-brown-wedding_wedding_media`, `newland-brown-wedding_caddy_data` and `_caddy_config`. On Mark's existing host, wedding media is a **bind mount instead**, so do not back up an unused old named media volume and assume it contains the photographs.

For a consistent database backup, stop only Memtly briefly and copy `/app/config` from the stopped container, then restart it. Keep credentials/backups private. Do not delete `.incoming-*` directories while uploads are running. Do not treat GUID upload directories as cache. Practise restoration in a separate deployment.

## Validation

CI builds the real Docker application, verifies login rendering and the webpack manifest fix, exercises original-byte integrity and a 2 GiB+1 MiB synthetic transfer, checks HEIC previews, capacity safeguards and ntfy retry behaviour, tests public/legacy access rules and rate limiting, and runs independent Chromium contexts through HTTPS Caddy including actual admin authentication, photo/video viewing copies, hide/close controls and restart persistence. Screenshots and build manifests are retained as CI artifacts. Review the result for the exact application commit; these checks are not an independent security audit or a replacement for testing the actual phones/hostname.

Core remains pinned through its gitlink; do not use `git submodule update --remote` for deployment. Neither master branch is changed. Upstream updates still require merging and testing the wedding integration before publishing a new deployment commit.
