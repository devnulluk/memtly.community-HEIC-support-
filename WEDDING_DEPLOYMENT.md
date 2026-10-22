# Mark & Keziah — private wedding photo postbox

**Deployment branch: `feature/wedding-guest-mode`. Do not deploy `master` or the earlier `feature/heic-originals` branch for the wedding portal.** Community pins the matching Core commit as a Git submodule. Neither master branch is changed.

## What this version does

Guests follow a private QR/link, optionally give a name, and upload photos/videos without accounts or an app. A protected browser cookie lets them see only that browser's uploads. The optional name is a label, never an access credential. Two people giving the same name do not gain access to each other's photographs.

The ivory/forest-green page uses a typographic M&K crest, serif headings, botanical flourishes and the couple's privacy wording. It has upload progress, manual retry, a thank-you panel and a private 'Your uploads' view. It does not include the original wedding website's bespoke image assets: those were not available in this repository.

Wedding media lives in **a separate private collection**, not Memtly's ordinary public gallery. The couple's collection is `/Wedding/Admin`. Only Memtly users with the Admin level can list every wedding upload, mark items reviewed, download originals and export a ZIP. Marking reviewed does not publish anything. No permanent-delete button is included in this first version.

## Before deployment

Use a Linux Docker host with the Docker Compose plugin, Git and OpenSSL. A dedicated VM with at least 4 GB RAM and ample storage is a sensible starting allocation; measure memory/disk use with real files. The application container has a 3 GB memory limit and runs as a non-root user. Run **one application replica only**: the on-disk metadata and admission limits are single-instance.

Choose a hostname, for example `photos.newland-brown.com`. Point its DNS A record at this host. Only add AAAA when IPv6 routing also works. Allow inbound TCP 80/443; UDP 443 is optional for HTTP/3. The bundled Caddy handles HTTPS. Those ports must not already be occupied by another proxy.

The Docker subnet `172.29.147.0/24` must not overlap an existing host/VPN/Docker network. If changing it, update both static service IPs and `Wedding__TrustedProxy` together. Do not trust arbitrary forwarded-header senders.

Check the **Wedding validation** workflow on GitHub for the exact commit you will deploy. A green build is not a replacement for the two-browser and real-phone acceptance checks below. The smoke host uses test-only authentication; it is not part of the production application.

## Install

```bash
git clone --branch feature/wedding-guest-mode --recurse-submodules \
  https://github.com/devnulluk/memtly.community-HEIC-support-.git memtly-wedding
cd memtly-wedding

# Substitute the actual hostname and an email address you control.
./deploy/setup.sh photos.newland-brown.com wedding@newland-brown.com

docker compose -f compose.wedding.yml config --quiet
docker compose -f compose.wedding.yml build --pull
docker compose -f compose.wedding.yml up -d
docker compose -f compose.wedding.yml logs --tail=100 memtly caddy
```

The setup script generates random credentials and `.env` with mode 600. It refuses to replace an existing `.env`. Save the printed password in your password manager. The initial Memtly **username is `admin`**, not the email address. The email is associated with that account. These credentials seed a fresh database; editing `ADMIN_PASSWORD` later does not reset an existing account's password.

The printed guest URL is of the form:

```text
https://photos.newland-brown.com/Wedding#invite=YOUR_RANDOM_SECRET
```

Put the **entire URL including `#invite=...`** into the QR code. The fragment keeps the invite out of normal HTTP request/access logs; JavaScript exchanges it for a protected cookie and removes it from the address bar. Do not commit this link, `.env`, data, or backups to GitHub. Anyone with the invitation link can upload, but does not acquire other guests' browser identities.

The couple's collection is:

```text
https://photos.newland-brown.com/Wedding/Admin
```

Sign in through Memtly with username `admin` and the generated password. Create a separate Admin-level account for Keziah through Memtly administration if you want distinct logins. Guest registration is disabled in the supplied deployment; guests never need it.

Do not expose the internal application port 5000 directly. Wedding media is outside all static file roots and is served through permission-checked controllers. Leave `Wedding__DevelopmentHttp` unset in production.

## Already using a reverse proxy?

The supplied file assumes a dedicated Caddy listener. Do not run it blindly over an existing proxy. Put your existing trusted proxy on the application network instead, remove/disable the Caddy service and host ports, set `Wedding__TrustedProxy` to that proxy's fixed IP, and forward to `memtly:5000` with the correct Host and `X-Forwarded-Proto: https`. Keep the application unreachable directly from the internet. Match upload size/timeouts and disable caching for `/Wedding/*`. Test secure cookies and authentication end-to-end. CDN/proxy request-size limits may be lower than the application limit.

## Storage, originals and limits

The named volumes are:

- `newland-brown-wedding_wedding_media`: originals, private JSON manifests, previews and temporary ZIP exports under `/data/wedding`.
- `newland-brown-wedding_memtly_config`: Memtly's SQLite database **and Data Protection keys**. Losing these keys invalidates guest cookies even if the media remains.
- `newland-brown-wedding_caddy_data` and `_caddy_config`: HTTPS/proxy state.

Defaults: 500 MiB per file, 5 GiB/1,000 uploads per anonymous browser, 50 GiB of originals in total. Browser upload queues are sequential; the server admits at most four concurrent uploads. The per-browser quota is not a per-person quota: a new browser identity gets a new allowance. The total limit still applies. Preview files, staging files and exports are additional disk usage; monitor free space and retain headroom. A ZIP export requires temporary free space roughly equal to the originals being exported. Do not run multiple large exports at once.

Change byte limits in `.env`, then recreate the application container. The 500 MiB per-file ceiling is enforced in code; lower limits are supported. Upload streams are copied directly to a private staging directory, hashed with SHA-256 and moved into place after validation. No resize, EXIF rewrite or re-encoding is applied to the original.

A lost upload response can be retried from the same page without duplicating a successfully saved file. Refreshing the page loses the browser's pending upload queue; this is not resumable multipart uploading across browser restarts. Keep the page open during a batch.

HEIC/HEIF gets a separate WebP viewing copy. Decoding has a timeout, concurrency limit and isolated temporary directory. Previews over 64 megapixels are refused while the original stays stored. The display copy is not an HDR archival substitute. HDR/gain-map fidelity, Live Photo still/video pairing and every multi-image HEIF variant are **not** guaranteed. Some MOV/HEVC clips will not play in every browser; they remain stored unchanged.

An iPhone's Photos picker can convert an image **before** the browser sends it. The server preserves the bytes received, not a hidden original it never received. For an unmodified-original check, export the original to Files and select it from there.

Guests who clear cookies, use private browsing or change browsers/devices lose their 'Your uploads' view; the couple still has the files. Optional names do not recover access. On a shared browser, the people using that browser share its upload identity. No cross-device account recovery/linking is implemented.

## Acceptance checks before printing/sharing the QR

1. On an iPhone, open the full private URL. Upload a HEIC from Files, a portrait/landscape image, a JPEG and a short video. Verify progress and the thank-you state. Check that the filename/type received is the one expected.
2. Open the same invitation in a genuinely separate browser/profile/device. It must show none of the first browser's uploads, even with the same optional name. Visiting a copied preview/video URL from that other browser must return 404.
3. Sign in as administrator and visit `/Wedding/Admin`. Confirm all items/names, review state, original download and ZIP export. Compare an exported original to the source with `sha256sum` or Windows `Get-FileHash -Algorithm SHA256`.
4. Restart the application container. Reopen both guest browsers and verify their separate histories remain. This tests the persisted keys and media volume.
5. Test invalid files and an interrupted upload. Keep the original on your phone until the site confirms it was saved. Test the actual QR on another phone before printing cards.

## Backups

Back up `.env`, **both application volumes**, and the deployment commit before upgrades. Keep a second copy off the server. Backups contain private wedding photographs and credentials; protect access and encrypt off-host copies.

For a consistent cold backup from the repository directory (temporary upload outage):

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

Check each command succeeds; restart Memtly even if a backup command fails. Do not run `docker compose down -v`: that deletes the volumes. Practise restoring into a separate test deployment, not over your only copy. Orphan `.incoming-*` directories from a hard crash can be removed only while the application is stopped and after confirming they are incomplete; never delete GUID upload directories as 'cache'.

## Updating and rollback

For updates already published to this wedding branch:

```bash
# Back up first, then preserve the current image for rollback.
docker image tag newland-brown-memtly:wedding newland-brown-memtly:before-update
git pull --ff-only
git submodule sync --recursive
git submodule update --init --recursive
docker compose -f compose.wedding.yml build --pull
docker compose -f compose.wedding.yml up -d --no-deps memtly
```

Do **not** use `git submodule update --remote`: deploy the reviewed/pinned Core commit. An upstream Memtly release must be merged into a development branch, tested with this private portal and then published; syncing a fork's master does not automatically update this feature branch. Core and Community are independent repositories, and updating Core also requires updating Community's gitlink.

To roll back the application image without deleting data:

```bash
docker image tag newland-brown-memtly:before-update newland-brown-memtly:wedding
docker compose -f compose.wedding.yml up -d --no-build --force-recreate --no-deps memtly
```

A future upstream database migration may also require restoring the matching database backup. Retain the original media and do not overwrite new uploads when restoring.

## Build dependencies and audit

The wedding Dockerfile builds libheif 1.23.1 from its checksum-verified official release with the HEVC decoder, and tests a public HEIC fixture during the build. It does not encode HEIC.

The first validation build revealed vulnerable inherited `System.Security.Cryptography.Pkcs` and `SQLitePCLRaw.lib.e_sqlite3` packages. The wedding image verifies the upstream lock graph, then enables isolated exact-version overrides in `Directory.Build.targets`: PKCS 10.0.13 and SQLitePCLRaw.bundle_e_sqlite3 3.0.5. This generates a separate production dependency lock graph, copied into `/app/build-manifests` for inspection. The upstream lockfiles remain untouched; updated production transitive versions are resolved during build rather than all being checked into this branch. Review these manifests and dependency advisories when updating. Do not mistake warnings from the initial upstream baseline restore for the final runtime graph; confirm the final graph too.

Official references: https://github.com/advisories/GHSA-846f-f764-76mx ; https://github.com/advisories/GHSA-2m69-gcr7-jv3q ; https://www.nuget.org/packages/SQLitePCLRaw.bundle_e_sqlite3/3.0.5 ; https://github.com/strukturag/libheif/releases/tag/v1.23.1 .

This is a custom fork, not an independently audited security product. The CI smoke tests cover selected owner-isolation/CSRF/integrity behaviours; real browser, reverse-proxy and phone acceptance remains necessary before inviting guests. No DNS or live server has been changed by publishing this branch.
