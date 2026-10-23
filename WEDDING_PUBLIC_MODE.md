# Public wedding uploads and shared viewing gallery

This release of `feature/wedding-guest-mode` implements the agreed **open wedding website**. It changes guest visibility deliberately: existing and new non-hidden wedding uploads have viewing copies in a shared gallery. Anyone who discovers the address can use it; there is no technical way to know they attended the event. The privacy wording is a request, not access control. Viewable previews can be saved or screenshotted.

## Guest experience

Open `https://wedding-photos.newland-brown.com/`. The address remains `/`: the server internally routes it to the wedding page. There is no invitation token, access code, account or app. JavaScript obtains a protected essential browser cookie and CSRF token automatically, then uploads the File object unchanged.

Guests can leave their name blank. A name entered under the new public-gallery notice is shared with that upload. Names on older manifests remain private because those were collected under a different notice. Administrators still see all submitted names. Guest listings do not expose original filenames, hashes, uploader identity tokens, notification state or internal paths.

Existing private invitation URLs continue to open the page, but are no longer required. Keep the existing `WEDDING_INVITE_KEY` in `.env`: it remains a server-side namespace for existing browser identities, not a key guests need. Do not regenerate secrets or create new config/media volumes during upgrade.

## Originals and viewing copies

Original HEIC/HEIF/JPEG/PNG/WebP/MP4/MOV files are stored byte-for-byte as received. Full originals and ZIP export remain administrator-only. The existing HEIC thumbnail/display conversion is retained.

Public video playback now uses a **separate MP4 viewing copy**, never the original. A viewing copy contains up to the **first 120 seconds**, downscaled to at most 1280 pixels on either side and 30 fps, with metadata/chapters/subtitles stripped. The full-length, full-quality original is unchanged and downloadable by the couple. This limit bounds preview processing on the small VPS, not guest upload size. It is shown in the guest page and video viewer. HDR preview fidelity is not guaranteed. Longer originals are not truncated.

One video conversion runs at a time, with two codec threads, a ten-minute timeout, input-dimension checks, a bounded output and reserved temporary disk capacity. Existing uploaded videos are queued when the new version starts. Failed previews leave the original stored and never fall back to serving that original publicly. Administrators can retry a failed preview.

## Administrator controls

Use `/Wedding/Admin` after signing into Memtly. The normal `/Account` dashboard is still separate from the wedding collection.

- **Close uploads / Reopen uploads** controls new upload requests. Transfers already accepted are allowed to finish. The gallery may stay open.
- **Close shared gallery / Reopen shared gallery** controls guest listings and preview/video routes, not just the UI. Administrators retain access.
- **Hide from gallery / Show in gallery** hides an individual item without deleting its original.
- **Reviewed** remains a private checklist, not a visibility switch.

These controls persist atomically in the private media directory and upload manifests. Unreadable/corrupt public settings fail closed. An administrator action requires a valid session and antiforgery token. Guest cookies do not confer administrator permissions. Names are not access credentials.

## Abuse protections

The public endpoints apply server-side limits before reading an upload body:

| Limit | Default |
|---|---:|
| New upload attempts per browser per minute | 60 |
| New upload attempts per source IP per minute | 240 |
| New guest-cookie sessions per IP per minute | 120 |
| Wedding requests per IP per minute | 3,000 |
| Simultaneous uploads per browser | 1 |
| Simultaneous uploads server-wide | 4 |

The IP budget is shared by people on the same Wi-Fi; the higher IP limit is intentional. IPv6 addresses in one /64 share a budget. Replacing a browser cookie or sending an untrusted X-Forwarded-For header does not create a new IP budget. Only forwarded headers from the configured Caddy proxy are trusted. The application port must remain unexposed directly to the internet.

The uploader waits on HTTP 429 and retries with the same request ID; retries do not duplicate a saved original. Requests still require a protected browser identity, CSRF token, same-origin checks when the browser supplies Origin/Fetch Metadata, permitted file extension/header, declared length, and enough available storage. Invalid file headers are rejected early. No arbitrary 500 MiB cap is reintroduced. Existing optional byte/count quotas, 10 GiB free-space reserve, idle-read timeout and streaming storage remain.

These are single-process, in-memory request limits, not a DDoS service or malware scanner. Restarting the app resets rate counters. Distributed abuse or a determined client can still consume storage/bandwidth until the disk safeguard/closed-upload switch intervenes. Monitor capacity and close submissions when no longer needed. Keep the app and native codecs updated.

Optional environment overrides (add to the Memtly service environment, not merely an unused `.env` key):

```yaml
WeddingPublic__Enabled: "true"
WeddingPublic__UploadsOpen: "true"
WeddingPublic__GalleryOpen: "true"
WeddingPublic__UploadsPerBrowserPerMinute: "60"
WeddingPublic__UploadsPerIpPerMinute: "240"
WeddingPublic__SessionsPerIpPerMinute: "120"
WeddingPublic__RequestsPerIpPerMinute: "3000"
```

All default to the values above without Compose edits. An environment false for uploads/gallery is a hard stop that the UI cannot override. Setting `WeddingPublic__Enabled=false` restores invitation-only, owner-only behaviour; preserve existing secrets/cookies. The existing owner-only video route in that legacy mode serves the uploader's original, as it did before; public mode never does.

## Update the existing Hetzner deployment

The repository's original `compose.wedding.yml` is intentionally unchanged in this update: the VPS already has a local media bind and marker guard. The updater preserves them byte-for-byte, together with `.env`. It incorporates the earlier local webpack fix into the tracked Dockerfile, so the login page is built correctly from a clean checkout.

Run during a quiet period, not while someone is uploading a long video. From the VPS as root:

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

The helper checks the expected volume UUID and marker, current bind/config mappings, branch ancestry and local changes. It refuses unrelated modifications or an upstream Compose change instead of guessing. It backs up the local Dockerfile/Compose/.env and retains the previous image under a rollback tag. It builds a candidate while the current app stays running, then refuses cutover when an `.incoming-*` directory exists. An orphan staging directory needs investigation; do not delete it blindly.

At cutover only Memtly is stopped. Its configuration directory (SQLite database/WAL and Data Protection keys) is cold-copied into a root-only backup directory, then the application is recreated using the same mounts. Caddy and other services are not restarted. Root/login/session checks must pass; otherwise the script attempts to restore the previous application image. A failed build leaves the running application unchanged. Review the printed errors if any check fails.

Backups are under `/root/memtly-deploy-backups/<timestamp>-<pid>`. They contain secrets and must not be shared or committed. This is a deployment/configuration backup, **not a second copy of all wedding media**; the existing media stays on the Hetzner volume. NAS replication and independent host monitoring remain separate work. Never use `docker compose down -v`.

## Verification

After deployment open the clean root on another browser or phone; upload a photo without a token; open the shared gallery from another browser; then sign into `/Wedding/Admin` and download the original. Confirm the upload ntfy notification still arrives. Public preview copies are deliberately shared; guest requests for Original/Export must lead to login or denial.

CI exercises the real production Docker image behind HTTPS Caddy, secure guest cookies, independent browser contexts, account-free root uploads, shared photo/video previews, actual Memtly administrator authentication, original byte comparisons, hide/close controls and restart persistence. It also runs the previous invitation-mode, HEIC, multi-gigabyte upload, disk-reserve and ntfy regression tests. Login HTTP 200 is now explicitly checked to catch missing embedded webpack manifests. Inspect the workflow result for the deployment commit; a green test is not an independent security audit or proof of every iPhone media variant.
