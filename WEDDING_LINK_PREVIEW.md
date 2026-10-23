# Wedding link preview

Share the clean homepage: https://wedding-photos.newland-brown.com/

The initial HTML now contains Open Graph and Twitter large-image card metadata, so a link-preview service can read it without JavaScript, an invitation, a guest cookie or an administrator login. The title is **Mark & Keziah — Wedding Photos**. The description starts **Share your memories**. All URL metadata points to the clean HTTPS homepage, without guest identities or tokens.

The approved white-rose/phone-cherub artwork is a standalone 1200 x 630 RGB JPEG at `/wedding/assets/social-preview-v1.jpg` (about 173 KiB). It is a public branding illustration, not a guest-upload preview. The versioned filename avoids reusing a previously cached image path. The image is committed in the repository and included by the existing Docker build: no manual asset copy, external image host, new service or Dockerfile change is required. Image optimisation applies only to this branding graphic, never guest originals.

Search-engine noindex directives, administrator authentication, protected original downloads, guest permissions and rate limiting remain unchanged. There is no crawler-specific authentication bypass. As with every public link-preview image, social providers can fetch and cache this branding artwork.

## Deploy

From the existing VPS checkout, during a quiet period:

```bash
cd /opt/memtly-wedding
bash deploy/upgrade-public.sh
```

The updater preserves local Compose, .env, Hetzner storage and application configuration, builds before cutover and retains the previous image for rollback. Caddy need not be changed. Publication to GitHub alone does not deploy the live site.

After deployment, send the clean homepage in a NEW message. Social apps may retain the old link preview for an already-shared URL, and an existing message may not update. Facebook's Sharing Debugger can request another scrape. Other messaging platforms choose their own caching and preview presentation; immediate refresh across all apps is not guaranteed. Do not share an admin or individual-media link for this card.

## Validation

`python3 tests/social_preview.py` checks the checked-in metadata and JPEG. With `--base-url https://wedding.test:5443 --ca-file /tmp/wedding-ci-root.crt`, it also tests the real production application behind HTTPS Caddy: initial HTML for root, /Wedding and a harmless share query, anonymous image GET/HEAD, five social-crawler user agents, robots accessibility and continued administrator protection. These are reproducible HTTP compatibility tests, not proof of a particular social provider's live cache or rendering.

The default title and artwork URL are tailored to this wedding hostname. A deployment under another domain should update the canonical/Open Graph/Twitter URL values together.
