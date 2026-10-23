#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
if [[ $# -ne 2 ]]; then
  echo "Usage: ./deploy/setup.sh photos.example.com admin@example.com" >&2
  exit 1
fi
if [[ -e .env ]]; then echo '.env already exists; leaving your secrets unchanged. See WEDDING_UPLOADS_AND_NTFY.md for upgrade settings.' >&2; exit 1; fi
if [[ ! $1 =~ ^[A-Za-z0-9][A-Za-z0-9.-]*\.[A-Za-z]{2,}$ || ! $2 =~ ^[A-Za-z0-9._+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}$ ]]; then
  echo 'Use a plain domain (no https://) and a valid administrator email.' >&2; exit 1
fi
command -v openssl >/dev/null || { echo 'Install openssl first.' >&2; exit 1; }
umask 077
invite=$(openssl rand -hex 32)
password="W!$(openssl rand -hex 24)"
# A separate random topic: do not disclose uploads/names on the router's topic.
ntfy_topic="wedding-media-$(openssl rand -hex 16)"
cat > .env <<EOF
WEDDING_DOMAIN=$1
ADMIN_EMAIL=$2
ADMIN_PASSWORD=$password
WEDDING_INVITE_KEY=$invite
MEMTLY_ENCRYPTION_KEY=$(openssl rand -hex 32)
MEMTLY_ENCRYPTION_SALT=$(openssl rand -hex 32)
MAX_FILE_BYTES=0
MAX_GUEST_BYTES=0
MAX_TOTAL_BYTES=0
MAX_GUEST_FILES=0
MIN_FREE_BYTES=10737418240
NTFY_ENABLED=true
NTFY_SERVER=https://ntfy.devnull.co.uk
NTFY_TOPIC=$ntfy_topic
NTFY_TOKEN=
NTFY_INCLUDE_NAMES=true
EOF
if [[ -d .git ]]; then
  for entry in '/.env' '/backups/' '/data/'; do grep -qxF "$entry" .git/info/exclude || printf '%s\n' "$entry" >> .git/info/exclude; done
fi
printf '\nPrivate guest/QR link (keep the #invite= part):\nhttps://%s/Wedding#invite=%s\n' "$1" "$invite"
printf '\nCouple’s collection:\nhttps://%s/Wedding/Admin\n\nInitial username: admin\nAdministrator email: %s\nInitial password: %s\n' "$1" "$2" "$password"
printf '\nSubscribe in ntfy:\nServer: https://ntfy.devnull.co.uk\nTopic: %s\n' "$ntfy_topic"
printf 'Protect the topic with ntfy access control; add NTFY_TOKEN to .env when required.\n'
printf '\nSecrets are saved in .env (mode 600). Do not commit or share it.\n'
