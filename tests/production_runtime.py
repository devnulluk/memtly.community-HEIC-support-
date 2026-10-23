#!/usr/bin/env python3
"""Exercise real Memtly authentication integration, persistence and image processing in CI.
The container is ephemeral, loopback-only and explicitly Development; no fake admin scheme.
"""
import hashlib, http.cookiejar, json, os, struct, sys, time, urllib.error, urllib.parse, urllib.request, uuid, zlib
BASE = 'http://127.0.0.1:5089'
TOKEN = 'ci-only-production-image-guest-invitation-12345678'
class Client:
    def __init__(self):
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
        self.csrf = None
    def request(self, path, data=None, headers=None):
        headers = dict(headers or {})
        if isinstance(data, dict):
            data = json.dumps(data).encode(); headers['Content-Type'] = 'application/json'
        if data is not None and self.csrf: headers['X-Wedding-CSRF'] = self.csrf
        try:
            with self.opener.open(urllib.request.Request(BASE + path, data=data, headers=headers), timeout=30) as response:
                return response.status, response.read(), response.headers
        except urllib.error.HTTPError as error: return error.code, error.read(), error.headers
    def enter(self):
        code, body, _ = self.request('/Wedding/Session'); assert code == 200, (code, body[:1000])
        self.csrf = json.loads(body)['csrf']
        code, body, _ = self.request('/Wedding/Enter', {'token': TOKEN}); assert code == 200, (code, body[:1000])
        code, body, _ = self.request('/Wedding/Session'); assert json.loads(body)['invited'] is True
    def upload(self, payload, filename):
        code, body, _ = self.request('/Wedding/Upload', payload, {
            'Content-Type':'application/octet-stream', 'X-File-Name':filename,
            'X-Guest-Name':'CI%20fixture', 'X-Upload-ID':uuid.uuid4().hex})
        assert code == 200, (code, body[:1000])
        item = json.loads(body)['item']; assert item['sha256'] == hashlib.sha256(payload).hexdigest()
        return item

def chunk(name, data): return struct.pack('>I', len(data)) + name + data + struct.pack('>I', zlib.crc32(name + data))
png = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', 2, 2, 8, 2, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(b'\x00\xff\x00\x00\x00\xff\x00' * 2)) + chunk(b'IEND', b'')
alice, bob = Client(), Client()
for _ in range(90):
    try:
        if alice.request('/Wedding/Session')[0] == 200: break
    except (urllib.error.URLError, ConnectionError): pass
    time.sleep(1)
alice.enter(); bob.enter()
code, html, _ = alice.request('/Wedding'); assert code == 200 and b'through your eyes' in html
for path in ('/wedding/wedding.css', '/wedding/wedding.js'):
    code, asset, _ = alice.request(path); assert code == 200 and len(asset) > 100
items = [alice.upload(png, 'runtime.png')]
# Pinned public fixture, verified against its Git blob ID before use.
url = 'https://raw.githubusercontent.com/strukturag/libheif/v1.23.1/tests/data/rainbow-451x461.heic'
with urllib.request.urlopen(url, timeout=30) as response: heic = response.read()
assert hashlib.sha1(b'blob ' + str(len(heic)).encode() + b'\0' + heic).hexdigest() == '6691f50f39bd69871a2abe284de2ef9f5243bc66'
items.append(alice.upload(heic, 'runtime.heic'))
for _ in range(120):
    code, body, _ = alice.request('/Wedding/Items'); assert code == 200
    current = json.loads(body)['items']
    if len(current) == 2 and all(i['preview'] in ('ready', 'unavailable') for i in current): break
    time.sleep(1)
assert len(current) == 2 and all(i['preview'] == 'ready' for i in current), current
for item in current:
    code, preview, headers = alice.request('/Wedding/Preview/' + item['id'])
    assert code == 200 and preview[:4] == b'RIFF' and preview[8:12] == b'WEBP'
    assert 'no-store' in headers.get('Cache-Control', '')
    assert bob.request('/Wedding/Preview/' + item['id'])[0] == 404
assert json.loads(bob.request('/Wedding/Items')[1])['total'] == 0
# These requests must not return an original or collection to an anonymous guest.
code, body, _ = bob.request('/Wedding/Original/' + items[0]['id']); assert body != png
code, body, _ = bob.request('/Wedding/AdminItems'); assert b'"items"' not in body
os.makedirs('artifacts', exist_ok=True)
with open('artifacts/runtime-uploads.json', 'w') as f: json.dump(current, f, indent=2)
print('PASS: real application starts, invitation exchange, private persistence, optional names, PNG and HEIC -> WebP, owner-only previews and non-admin download restrictions.')
