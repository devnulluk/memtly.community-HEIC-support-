#!/usr/bin/env bash
# CI ONLY: isolated fake domain, disposable containers and synthetic media.
set -euo pipefail
mkdir -p artifacts
export WEDDING_TEST_PASSWORD="CI!$(openssl rand -hex 24)"
export NODE_PATH=/tmp/wedding-browser/node_modules
cleanup() {
  docker logs --tail 100 wedding-public || true
  docker rm -f wedding-public wedding-test-proxy >/dev/null 2>&1 || true
  docker network rm wedding-public-ci >/dev/null 2>&1 || true
  rm -f artifacts/public-browser-state.json
}
trap cleanup EXIT
printf '127.0.0.1 wedding.test\n' | sudo tee -a /etc/hosts >/dev/null
cat > /tmp/Caddyfile.wedding-ci <<'EOF'
https://wedding.test:5443 {
    tls internal
    reverse_proxy wedding-public:5000
}
EOF
docker network create --subnet 172.29.148.0/24 wedding-public-ci >/dev/null
docker run -d --name wedding-public --network wedding-public-ci --ip 172.29.148.3 \
  -e ASPNETCORE_ENVIRONMENT=Production -e Wedding__Enabled=true \
  -e WeddingPublic__Enabled=true -e Wedding__PublicUrl=https://wedding.test:5443 \
  -e Wedding__TrustedProxy=172.29.148.2 \
  -e Wedding__InviteKey=ci-only-public-identity-namespace-12345678 \
  -e Wedding__StoragePath=/data/wedding -e Wedding__MinFreeBytes=1048576 \
  -e Memtly__Base_Url=https://wedding.test:5443 -e Memtly__Force_Https=true \
  -e Memtly__Account__Admin__Password="$WEDDING_TEST_PASSWORD" \
  -e Memtly__Account__Admin__Email=ci@example.invalid \
  -e Memtly__Security__Encryption__Key="$(openssl rand -hex 32)" \
  -e Memtly__Security__Encryption__Salt="$(openssl rand -hex 32)" \
  -e Memtly__Account__Registration__Enabled=false \
  -e Memtly__Logging__Graylog__Enabled=false \
  memtly-wedding:test >/dev/null
docker run -d --name wedding-test-proxy --network wedding-public-ci --ip 172.29.148.2 \
  -p 127.0.0.1:5443:5443 -v /tmp/Caddyfile.wedding-ci:/etc/caddy/Caddyfile:ro caddy:2 >/dev/null
for attempt in $(seq 1 90); do
  if curl --noproxy '*' -kfsS https://wedding.test:5443/Wedding/Session >/dev/null; then break; fi
  sleep 1
done
docker cp wedding-test-proxy:/data/caddy/pki/authorities/local/root.crt /tmp/wedding-ci-root.crt >/dev/null
curl --noproxy '*' --cacert /tmp/wedding-ci-root.crt -fsS https://wedding.test:5443/ >/dev/null
curl --noproxy '*' --cacert /tmp/wedding-ci-root.crt -fsS https://wedding.test:5443/Account/Login >/dev/null
python3 tests/social_preview.py --base-url https://wedding.test:5443 --ca-file /tmp/wedding-ci-root.crt | tee artifacts/social-preview-checks.txt
# Use a real PNG encoder and photographic-sized dimensions for the browser
# authorization test. Successful preview generation remains mandatory; do not
# substitute an uploaded original or bypass the application preview worker.
docker run --rm --user "$(id -u):$(id -g)" --entrypoint ffmpeg \
  -v "$PWD/artifacts:/fixtures" memtly-wedding:test \
  -hide_banner -loglevel error -f lavfi -i testsrc=size=640x480:rate=1 \
  -frames:v 1 -c:v png -pix_fmt rgb24 -threads 1 -update 1 \
  /fixtures/browser-original.png
docker run --rm --user "$(id -u):$(id -g)" --entrypoint ffmpeg \
  -v "$PWD/artifacts:/fixtures" memtly-wedding:test \
  -hide_banner -loglevel error -f lavfi -i testsrc=size=320x240:rate=24 \
  -t 1 -c:v libx264 -pix_fmt yuv420p -threads 1 \
  -metadata comment=ORIGINAL_METADATA_NOT_FOR_PUBLIC_PREVIEWS \
  /fixtures/browser-original.mp4
npm install --prefix /tmp/wedding-browser --no-audit --no-fund playwright@1
node /tmp/wedding-browser/node_modules/playwright/cli.js install --with-deps chromium
node tests/public_browser.cjs
docker restart wedding-public >/dev/null
for attempt in $(seq 1 90); do
  if curl --noproxy '*' --cacert /tmp/wedding-ci-root.crt -fsS https://wedding.test:5443/Wedding/Session >/dev/null; then break; fi
  sleep 1
done
node tests/public_restart.cjs
