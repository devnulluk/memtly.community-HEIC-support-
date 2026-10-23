# Mark & Keziah — private wedding photo postbox

**Deploy `feature/wedding-guest-mode`, not `master` or the earlier HEIC-only branch.** Community pins its matching Core commit as a submodule. Neither master branch has been changed.

**Latest additions:** automatic Let's Encrypt certificates, storage-aware large-video uploads without fixed quotas, and durable ntfy upload alerts. See [upload, HTTPS and ntfy settings](WEDDING_UPLOADS_AND_NTFY.md), especially when updating an existing `.env`.

## Guest and couple experience

Guests follow a private QR/link, optionally give a name and upload photos/videos without an account or app. A protected cookie lets them see only their browser's uploads. The optional name is a label, never a credential: matching names do not combine identities.

The ivory/forest-green page uses a typographic M&K crest, serif headings, botanical flourishes and the agreed household-only sharing wording. It provides upload progress, manual retry, a thank-you panel and a private own-upload view. It is styled to match the wedding's visual language but does not contain the original website's bespoke image assets.

Wedding media lives in **a separate private collection**, not Memtly's ordinary public gallery. The couple's collection is `/Wedding/Admin`. Only Memtly Admin-level users can list every upload/name, mark items reviewed, download originals or export a ZIP. Reviewed is a private checklist, not a publishing switch. No permanent-delete button is included.

## Host and networking

Use a Linux Docker host with the Docker Compose plugin, Git and OpenSSL. A dedicated VM with at least 4 GB RAM and ample storage is a starting allocation, not a performance guarantee. The app has a 3 GB container memory limit and runs non-root. Run **one application replica**; metadata/admission/notification coordination is single-instance.

Choose a hostname, for example `photos.newland-brown.com`. Its DNS A record must reach your host. Add AAAA only when IPv6 also routes correctly. The bundled Caddy needs TCP 80/443; UDP 443 is optional for HTTP/3. It explicitly uses Let's Encrypt to obtain/renew certificates and redirects HTTP to HTTPS. No Certbot or renewal cron is needed. Persist its data volume.

Ports 80/443 must not already belong to another proxy. The Docker subnet `172.29.147.0/24` must not overlap another host, VPN or Docker network. Changing it requires changing both static service IPs and `Wedding__TrustedProxy` together. Do not trust arbitrary forwarded headers.

## Fresh installation

```bash
git clone --branch feature/wedding-guest-mode --recurse-submodules \
  https://github.com/devnulluk/memtly.community-HEIC-support-.git memtly-wedding
cd memtly-wedding

# Substitute the hostname and administrator email you control.
./deploy/setup.sh photos.newland-brown.com wedding@newland-brown.com

docker compose -f compose.wedding.yml config --quiet
docker compose -f compose.wedding.yml build --pull
docker compose -f compose.wedding.yml up -d
docker compose -f compose.wedding.yml logs --tail=100 memtly caddy
```

Setup generates random credentials, invitation key and ntfy topic; writes `.env` with mode 600; and refuses to overwrite an existing file. Save the password securely. The initial username is **`admin`**, not the email address. Editing `ADMIN_PASSWORD` later does not reset an existing account's password.

It prints the complete guest URL:

```text
https://photos.newland-brown.com/Wedding#invite=YOUR_RANDOM_SECRET
```

Encode the **entire printed URL, including `#invite=...`**, in the QR. JavaScript exchanges the fragment for a protected browser cookie and removes it from the address bar. The fragment keeps the invite out of ordinary HTTP/access logs. Anyone with the private link can upload, but does not gain another browser's identity. Keep it, `.env`, private media and backups out of GitHub.

The couple's collection is:

```text
https://photos.newland-brown.com/Wedding/Admin
```

Sign in through Memtly with username `admin` and the generated password. A separate Admin-level account for Keziah can be created through administration. Public guest registration is disabled in this deployment; account linking/cross-device recovery is not implemented.

Do not expose application port 5000 directly or set `Wedding__DevelopmentHttp` in production. Wedding files are outside every static-media root and are served through permission-checked routes.

## ntfy

Fresh setup enables alerts to `https://ntfy.devnull.co.uk` using a separate random `wedding-media-...` topic, printed by setup and stored in `.env`. Subscribe to that exact topic in your ntfy app with your server selected. It does not reuse the router's `wedding_server` topic or change server ACLs.

Each committed file queues a message containing name (when supplied/enabled), type, filename and size. Tapping opens the administrator collection, still requiring login. No media, cookies or invitation secrets are attached. Network failure never rolls back an upload: notification state is persisted with the file and retried after failures/restarts. Delivery is at-least-once, so a lost acknowledgement can cause a duplicate. A batch currently sends one alert per file.

Protect the topic with ntfy access control and put a publisher token in `NTFY_TOKEN` when required. `NTFY_INCLUDE_NAMES=false` omits names; filenames remain. Consider lock-screen visibility. HTTPS tokens stay server-side. See [the detailed guide](WEDDING_UPLOADS_AND_NTFY.md) for every setting and upgrade instructions.

## Original quality, capacity and transfer behaviour

Fresh defaults have no application file-size, per-browser-byte, gallery-byte or file-count quota: the MAX_* settings are zero. Positive values restore optional quotas. Existing `.env` files retain their previous caps until edited. The old 500 MiB ceiling, bundled Caddy body cap and browser's 30-minute cutoff have been removed.

The default `MIN_FREE_BYTES=10737418240` retains **10 GiB free** on the filesystem containing `/data/wedding`. At most four uploads are admitted concurrently; capacity is reserved using declared file sizes and rechecked during transfer. Files are streamed in bounded buffers, SHA-256 hashed, validated and committed from private staging without recompressing or rewriting the original. Disk-backed ZIP exports also require sufficient temporary capacity.

This is a safeguard, not infinite storage or an operating-system quota. Other processes, previews, filesystem metadata and logs can use space. Monitor the actual filesystem and maintain backups/headroom. External CDN/proxy limits may still reject large requests even when the application has no quota.

Retries use a stable request ID within the page to avoid duplicating an already-saved upload whose response was lost. Interrupted files restart from the beginning; uploads are not resumable across browser restarts. Keep the page open and screen awake, ideally on Wi-Fi. Idle-read/network protections still apply. Avoid deployments while guests are uploading.

HEIC/HEIF originals are kept separately from WebP previews. Decoding is bounded; unsupported/failed previews do not remove the stored original. Preview decoding over 64 megapixels is refused. HDR/gain-map fidelity in WebP, Live Photo pairing and all multi-image HEIF variants are not guaranteed. Some original MOV/HEVC files cannot play in every browser but remain stored.

The server preserves the bytes it receives. The iPhone Photos picker may convert before upload. Export an unmodified original to Files and upload that for an integrity check. Clearing cookies/changing devices loses a guest's view, not the couple's files. Shared browsers share an identity; names cannot recover access.

## Existing reverse proxy

Do not launch the bundled Caddy over an existing listener. Put the existing trusted proxy on the application network, omit the Caddy service/host ports, set `Wedding__TrustedProxy` to the proxy's fixed IP and forward to `memtly:5000` with the correct Host and `X-Forwarded-Proto: https`. Keep the app unreachable directly from the internet. Remove/raise that proxy's body-size/time limits and disable caching for `/Wedding/*`. Test HTTPS, cookies and uploads end-to-end. Certificate management can stay with that proxy instead.

## Storage and backups

The volumes are:

- `newland-brown-wedding_wedding_media`: private originals, manifests (including notification state), previews and temporary exports.
- `newland-brown-wedding_memtly_config`: SQLite database and Data Protection keys; losing the keys invalidates guest cookies.
- `newland-brown-wedding_caddy_data` and `_caddy_config`: certificate/proxy state.

Back up `.env`, **both application volumes**, and the deployment commit before upgrades; retain an encrypted off-server copy. Do not use `docker compose down -v`, which deletes the volumes.

A consistent cold backup from the repository directory causes a temporary upload outage:

```bash
umask 077
mkdir -p backups
stamp=$(date -u +%Y%m%dT%H%M%SZ)
docker compose -f compose.wedding.yml stop memtly

docker run --rm --user 0 --entrypoint tar \
  -v newland-brown-wedding_wedding_media:/source:ro \
  -v "$PWD/backups:/backup" newland-brown-memtly:wedding \
  -C /source -czf "/backup/wedding-media-$stamp.tar.gz" .
docker run --rm --user 0 --entrypoint tar \
  -v newland-brown-wedding_memtly_config:/source:ro \
  -v "$PWD/backups:/backup" newland-brown-memtly:wedding \
  -C /source -czf "/backup/memtly-config-$stamp.tar.gz" .
cp .env "backups/environment-$stamp.env"
git rev-parse HEAD > "backups/commit-$stamp.txt"
docker compose -f compose.wedding.yml start memtly
```

Check command results and restart the app even if a backup command fails. Back up Caddy state as well to retain its account/certificates. Practise restoration separately, not over the only copy. Orphan `.incoming-*` directories after a hard crash should only be removed with the app stopped after confirming they are incomplete; GUID upload directories are not cache.

## Updates and rollback

```bash
# Back up first; preserve the working image.
docker image tag newland-brown-memtly:wedding newland-brown-memtly:before-update
git pull --ff-only
git submodule sync --recursive
git submodule update --init --recursive
# Existing installations: update quota/ntfy settings in .env as documented.
docker compose -f compose.wedding.yml config --quiet
docker compose -f compose.wedding.yml build --pull
docker compose -f compose.wedding.yml up -d --no-deps memtly
docker compose -f compose.wedding.yml exec caddy caddy reload --config /etc/caddy/Caddyfile
```

Do not use `git submodule update --remote`; deploy the reviewed Core commit. An upstream release must be merged/tested on a development branch before updating this fork's wedding branch. Updating a fork's master does not automatically update a feature branch. Changes to Core also require a new pinned Community gitlink.

Image rollback without deleting data:

```bash
docker image tag newland-brown-memtly:before-update newland-brown-memtly:wedding
docker compose -f compose.wedding.yml up -d --no-build --force-recreate --no-deps memtly
```

A future database migration may require a matching database backup. Keep all new originals and do not overwrite them when restoring. Retain the corresponding `.env`/proxy configuration for rollback too.

## Build dependencies and validation

The image builds checksum-verified libheif 1.23.1 with HEVC decoding and tests a public fixture. The inherited dependency graph is first restored in locked mode, then isolated exact-version wedding security overrides select PKCS 10.0.13 and SQLitePCLRaw.bundle_e_sqlite3 3.0.5. The final resolved lock graphs are retained in `/app/build-manifests` and CI artifacts; upstream lockfiles remain unchanged. Review dependency advisories/manifests on updates; this is not a claim of a comprehensive security audit.

The Wedding validation workflow tests access/CSRF/identity isolation, original integrity, failure retention, ZIP export, disk reservations, ntfy retry/backoff/restart, Caddy's Let's Encrypt configuration, real application startup and PNG/HEIC previews. The size boundary test transmits 2 GiB + 1 MiB of synthetic MP4-shaped data and checks SHA-256; it is not a playable-video/real-iPhone compatibility test. Check the run result for the exact commit. Tests never request your real domain certificate or contact the live ntfy server.

Before sharing the QR, test your actual HTTPS host on an iPhone and a separate browser/device. Check a HEIC from Files, real large video, original download/hash, private collection isolation, administrator access, restart persistence and ntfy delivery. Verify the printed QR on another phone. No DNS or live server is changed by publishing this branch.
