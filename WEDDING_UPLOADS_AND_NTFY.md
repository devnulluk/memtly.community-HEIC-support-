# Let's Encrypt, full-quality videos and ntfy

These features are on `feature/wedding-guest-mode`. Neither master branch is changed.

## HTTPS

The supplied Caddyfile explicitly selects Let's Encrypt's production ACME directory. Caddy obtains and renews the certificate and redirects HTTP to HTTPS; no separate Certbot container or renewal cron is needed.

Set `WEDDING_DOMAIN` to the real hostname and `ADMIN_EMAIL` to an address you control. DNS must point at the deployment; TCP ports 80/443 must reach Caddy for the bundled public validation setup. Keep the Caddy data volume. A localhost/internal hostname does not get a publicly trusted Let's Encrypt certificate this way. An existing reverse proxy can instead manage the certificate: do not run two proxies on the same host ports. CI validates the Caddy configuration but never requests a real certificate for your domain.

## File sizes: zero means no configured quota

Fresh setup now writes:

```dotenv
MAX_FILE_BYTES=0
MAX_GUEST_BYTES=0
MAX_TOTAL_BYTES=0
MAX_GUEST_FILES=0
MIN_FREE_BYTES=10737418240
```

Zero disables that particular application quota. It does **not** promise infinite storage or bypass filesystem, browser, network or third-party proxy limitations. A positive byte/count value reinstates a quota. The earlier 500 MiB hard ceiling is gone, as are the bundled Caddy body-size cap and browser's 30-minute wall-clock cutoff. Only the wedding upload action overrides Kestrel's body-size limit; small control requests remain limited.

Video bytes are streamed directly to private storage and hashed, not loaded into a giant memory buffer or recompressed. The default 10 GiB free-space reserve is kept on the filesystem containing `/data/wedding`. At most four uploads are active at once. Declared file sizes reserve capacity across concurrent uploads; the exact received size is verified and free space is rechecked during transfer. ZIP exports also reserve their temporary disk requirements. An upload cannot consume the reserve merely by declaring a smaller file. HTTP transports without Content-Length use the browser-supplied X-File-Size; a stream with no declared size is refused rather than accepted without a capacity check.

The reserve is a best-effort application guard, not a hard filesystem quota: other containers, previews, logs, filesystem metadata and external writers can use space. Monitor the actual storage filesystem and keep backups. A dedicated disk/volume is preferable to filling an operating-system disk. `MinFreeBytes` cannot be disabled; its minimum allowed value is 1 MiB, though that is far too small for a production safety margin.

An interrupted transfer is retried from the beginning. This version does not implement cross-restart resumable/chunked *application* uploads. HTTP transfer chunking is supported but is not resume support. Keep the phone's page open and screen awake, preferably on Wi-Fi. The server still rejects stalled requests (a read with no data for two minutes) and retains its network-level protections. Do not deploy/restart in the middle of guests uploading long videos.

The server preserves what the device sends. A browser/Photos picker may transform media before upload; exporting an unmodified original to Files is the reliable integrity-test path. Some original MOV/HEVC videos cannot play in every browser even though their files are safely stored.

## ntfy upload notifications

Each successfully committed file gets one queued notification when ntfy is enabled. Preview generation is not a prerequisite; a file can be safely archived before its preview is available. Notification intent is stored in the private upload manifest, so a restart does not lose a pending message. The upload response does not wait for ntfy. Failed sends/timeouts are retried with exponential backoff (up to an hour between ordinary retries, respecting a longer Retry-After).

An upload retry with the same request ID does not create another notification. Delivery is **at-least-once**, not exactly-once: if ntfy accepts a message but its response is lost, a retry can produce a duplicate. Previously uploaded legacy files and files received while ntfy is disabled do not suddenly generate notifications when enabled later. Already-pending notifications pause when disabled and resume when re-enabled.

The message contains the optional name (or 'A guest'), photo/video type, filename, size and a short reference. Tapping it opens `/Wedding/Admin`, which still requires administrator login. No original, thumbnail, guest cookie, invitation secret or token is attached or included in that URL. A large batch currently generates a message for each file, not a batch summary.

Fresh setup uses your existing server but generates a **separate random photo-upload topic**, not the router's `wedding_server` topic:

```dotenv
NTFY_ENABLED=true
NTFY_SERVER=https://ntfy.devnull.co.uk
NTFY_TOPIC=wedding-media-YOUR_RANDOM_TOPIC_SUFFIX
NTFY_TOKEN=
NTFY_INCLUDE_NAMES=true
```

Subscribe to the exact topic printed by `deploy/setup.sh` using the ntfy app with your server selected. These are operator environment settings, not controls exposed to guests. `NTFY_SERVER` is the server root, not a URL with the topic appended. A server access token is optional when anonymous publishing is permitted; otherwise put a publishing token in `NTFY_TOKEN`. Tokens are sent in a Bearer header over HTTPS and are not committed to Git or sent to guests. Redirects are not followed by the notifier.

**Protect the topic with ntfy access control, especially because it can contain names and filenames.** A random topic is not a substitute for access control if its URL is leaked. Choose a publisher-only credential for the application and give only you/Keziah read access. Set `NTFY_INCLUDE_NAMES=false` to omit the optional name; filenames still appear. Lock-screen notification previews can reveal message text, so configure the phone accordingly.

We have not altered your ntfy server, its ACLs, subscriptions or credentials, and tests do not contact your live ntfy server. A successful publish confirms server acceptance, not delivery to a particular phone.

## Upgrading an existing checkout

Back up data/config first. Do not rerun setup or replace your existing secrets/invitation key.

```bash
git pull --ff-only
git submodule sync --recursive
git submodule update --init --recursive
nano .env
```

Change existing MAX_FILE_BYTES/MAX_GUEST_BYTES/MAX_TOTAL_BYTES values to zero, add MAX_GUEST_FILES=0 and MIN_FREE_BYTES=10737418240, and add the ntfy settings above. Generate a private topic suffix with `openssl rand -hex 16`; put the actual topic in `.env`, not in Git. Do not leave duplicate old/new entries in `.env`.

```bash
docker compose -f compose.wedding.yml config --quiet
docker compose -f compose.wedding.yml build --pull
docker compose -f compose.wedding.yml up -d --no-deps memtly
# Reload the bind-mounted Caddyfile to remove the old proxy cap and select Let's Encrypt.
docker compose -f compose.wedding.yml exec caddy caddy reload --config /etc/caddy/Caddyfile
```

For a fresh deployment, follow `WEDDING_DEPLOYMENT.md`; setup generates these defaults automatically. Existing `.env` quotas remain in effect until changed, even with the new code. Do not use `docker compose down -v`.

## Validation and acceptance

The workflow includes tests for configured/unlimited quotas, physical-space reservations, original retention, durable ntfy retry/backoff/restart, token/root URL formatting and private admin links. It also transmits a **2 GiB + 1 MiB synthetic MP4-shaped file** through the actual running application and compares SHA-256, exceeding both prior size ceilings. This boundary fixture is not a playable video and does not prove iPhone codec/browser compatibility. Check the workflow result for the specific deployment commit rather than assuming all checks passed.

On the actual host: confirm the Let's Encrypt certificate, subscribe to your private ntfy topic, upload a real large video and verify the original download/hash and notification. Briefly simulate an unavailable ntfy endpoint only in a test deployment to confirm upload success and delayed notification recovery. No real domain certificate or live ntfy delivery has been verified by CI.

Official references: https://caddyserver.com/docs/automatic-https ; https://caddyserver.com/docs/caddyfile/options#acme_ca ; https://docs.ntfy.sh/publish/#publish-as-json ; https://docs.ntfy.sh/publish/#authentication ; https://learn.microsoft.com/aspnet/core/mvc/models/file-uploads .
