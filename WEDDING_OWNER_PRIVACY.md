# Open uploads with private per-browser viewing (23 October 2026)

This supersedes the shared-gallery default in WEDDING_PUBLIC_MODE.md and earlier release notes. Public access now means anyone can submit via the root URL without an invitation or account, **not** that visitors may view one another's uploads.

## Default behaviour

- `WeddingPublic__Enabled=true` keeps the clean root and automatic protected guest cookie.
- New `WeddingPublic__SharedGallery=false` is the default, independent of open uploads and the persisted GalleryOpen flag. No Compose or .env changes are needed on the existing wedding deployment.
- `/Wedding/Items` filters by the protected browser owner ID before counts and pagination. Original guest names, existing ShareName consent, query parameters and a common IP cannot confer access to another browser's items.
- `/Wedding/Preview/{id}` and `/Wedding/Video/{id}` enforce the same owner policy, including requests with Range. Knowing a media ID or retaining an old link is not enough. No original-file fallback is added.
- `/Wedding/Admin` and authenticated administrator media access still include all uploads. The ordinary guest listing remains owner-only even for a signed-in administrator; full collection access is separate.
- Names remain optional, and private to the contributor and couple. The UI says Your uploads and discloses the browser-cookie limitation under About your files.
- Existing media, owner IDs, protected cookies, keys, settings and originals are preserved. Do not clear cookies or rotate the invitation namespace to apply this fix. Different devices or private windows have different collections; the administrator can still see earlier uploads after a cookie is lost.

Shared viewing now requires an explicit operator setting `WeddingPublic__SharedGallery=true`. No browser/admin UI switch changes that setting. Keep it unset/false for this wedding. Older `GalleryOpen=true` settings and ShareName metadata do not enable sharing.

## Immediate containment and deployment

Before deploying, use the current `/Wedding/Admin` **Close shared gallery** button. This stops new guest listing/preview/video access without deleting files or closing uploads. It cannot retract images already delivered or saved on somebody's device, and administrators retain full access.

During a quiet upload period:

```bash
cd /opt/memtly-wedding
bash deploy/upgrade-public.sh
```

Use a fresh root-page request to confirm the installed policy:

```bash
curl -fsS https://wedding-photos.newland-brown.com/Wedding/Session | python3 -c 'import json,sys; s=json.load(sys.stdin); assert s.get("publicAccess") is True and s.get("sharedGallery") is False; print("Open uploads; private per-browser viewing confirmed")'
```

Only after that check succeeds, reopen the admin page and use **Reopen guest previews** if you closed the gallery. This enables only each browser's own previews, not shared viewing. Reload both guest pages; test with separate non-admin browser contexts. An administrator is intentionally able to access everyone's media from the administrator collection.

The existing updater retains local Compose, .env, the Hetzner media guard/mount and the old image. It does not change Caddy or deploy automatically when GitHub is updated. Keep adequate free space on the system disk for the Docker build.

## Regression checks

The smoke suite exercises invitation-only access, open owner-only access and explicitly opted-in sharing separately. It checks exact photo/video IDs, Range requests, empty/replaced identities, forged owner/name headers/query parameters, existing ShareName metadata, counts, administrator-only originals and controls, and close/reopen behaviour. The production HTTPS browser test uploads through two independent contexts on the same IP, checks cross-browser denial for real generated PNG/video previews, verifies actual administrator login and all-upload access, and repeats owner isolation after restart. Existing HEIC, large-upload, storage, ntfy, design and social-card tests remain enabled.
