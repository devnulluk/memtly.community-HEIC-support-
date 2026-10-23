# Approved botanical and tech-cherub artwork

Implements the couple's approved preview: white roses/green foliage, a phone-using cherub and a bespectacled cherub connecting a modem/router. The decorative assets are isolated from the approved mockup, background-masked and resized for the web. They are not a flattened screenshot of the page: all text and upload controls remain real HTML and functional.

The flower image has transparent padding and a softened outer edge. Both flower canvases are contained inside the page instead of positioning them off-screen. The right bouquet mirrors the left. On phones the flowers sit beside the crest, above the heading. The cherubs flank the sharing reminder on desktop and sit in a separate row above it on narrow screens, so they do not cover readable text or controls. Decorative images have empty alt text, aria-hidden and pointer-events:none. No new third-party requests are introduced.

Only artwork, markup, an additive stylesheet and visual regression assertions change. Backend upload handling, byte-preserved originals, quotas, privacy/access controls, optional names, ntfy, database, Dockerfile, Compose, credentials and the update helper are unchanged. The three new WebP assets total approximately 32 KiB. Browser checks cover loaded assets, 320/390/768/1440px widths, unclipped bounds, no artwork/text/control overlap and the upload chooser remaining on the initial screen. Existing full application tests still apply.

After publication to feature/wedding-guest-mode, the existing installation can use:

```bash
cd /opt/memtly-wedding
bash deploy/upgrade-public.sh
```

Run during a quiet period. This uses the existing backup/rollback procedure and preserves the local media bind/marker guard and .env. Publishing code does not update the live VPS; that command performs the deployment. The master branch and Core repository are not modified.
