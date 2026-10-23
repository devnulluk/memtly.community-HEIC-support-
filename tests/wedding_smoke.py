#!/usr/bin/env python3
"""Access-control, integrity, failure and retry checks against the TEST-ONLY host."""
import base64, hashlib, http.cookiejar, io, json, sys, time, urllib.error, urllib.parse, urllib.request, uuid, zipfile
BASE = sys.argv[1] if len(sys.argv) > 1 else 'http://127.0.0.1:5088'
TOKEN = 'test-only-invitation-not-for-deployment-12345'
PNG = base64.b64decode('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aD1sAAAAASUVORK5CYII=')
class Client:
    def __init__(self, admin=False):
        self.jar = http.cookiejar.CookieJar()
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.jar))
        self.admin = admin
        self.csrf = None
    def request(self, path, data=None, headers=None, method=None):
        h = dict(headers or {})
        if self.admin: h['X-Test-Admin'] = 'test-host-only'
        if isinstance(data, dict):
            data = json.dumps(data).encode(); h['Content-Type'] = 'application/json'
            if self.csrf: h['X-Wedding-CSRF'] = self.csrf
        req = urllib.request.Request(BASE + '/Wedding/' + path, data=data, headers=h, method=method)
        try:
            with self.opener.open(req, timeout=20) as r: return r.status, r.read(), r.headers
        except urllib.error.HTTPError as e: return e.code, e.read(), e.headers
    def session(self):
        code, body, _ = self.request('Session'); assert code == 200, (code, body)
        result = json.loads(body); self.csrf = result['csrf']; return result
    def enter(self):
        self.session(); code, body, headers = self.request('Enter', {'token': TOKEN}); assert code == 200, body
        assert 'httponly' in headers.get('Set-Cookie', '').lower()
        assert self.session()['invited']
    def upload(self, payload=PNG, filename='memory.PNG', name='', request_id=None, csrf=True):
        headers = {'Content-Type':'application/octet-stream', 'X-File-Name':urllib.parse.quote(filename),
                   'X-Guest-Name':urllib.parse.quote(name), 'X-Upload-ID':request_id or uuid.uuid4().hex}
        if csrf: headers['X-Wedding-CSRF'] = self.csrf
        return self.request('Upload', payload, headers)

alice, bob, outsider, admin = Client(), Client(), Client(), Client(True)
assert outsider.request('Items')[0] == 403
outsider.session(); assert outsider.request('Enter', {'token': 'wrong'})[0] == 403
assert outsider.request('Enter', b'{"token":"' + TOKEN.encode() + b'"}', {'Content-Type':'application/json'})[0] == 400
alice.enter(); bob.enter(); admin.session()
request_id = uuid.uuid4().hex
code, body, _ = alice.upload(name='Same name', request_id=request_id); assert code == 200, (code, body)
item = json.loads(body)['item']; ident = item['id']
assert item['sha256'] == hashlib.sha256(PNG).hexdigest()
assert alice.upload(csrf=False)[0] == 400
assert bob.upload(b'<html>not a photo</html>', 'pretend.jpg')[0] == 415
assert json.loads(bob.request('Items')[1])['total'] == 0
assert bob.request('Preview/' + ident)[0] == 404
assert bob.request('Video/' + ident)[0] == 404
assert bob.request('Original/' + ident)[0] in (401, 403)
assert bob.request('AdminItems')[0] in (401, 403)
assert bob.request('Export')[0] in (401, 403)
assert bob.request('Review/' + ident, {'reviewed': True})[0] in (401, 403)
code, repeat, _ = alice.upload(name='Same name', request_id=request_id); assert code == 200
assert json.loads(repeat)['item']['id'] == ident
assert json.loads(alice.request('Items')[1])['total'] == 1
code, original, _ = admin.request('Original/' + ident); assert code == 200 and original == PNG
assert admin.request('Review/' + ident, {'reviewed': True})[0] == 200
assert json.loads(admin.request('AdminItems')[1])['items'][0]['reviewed']
# Matching optional display names do not merge identities.
code, other, _ = bob.upload(name='Same name'); assert code == 200
other_id = json.loads(other)['item']['id']; assert other_id != ident
assert {i['id'] for i in json.loads(alice.request('Items')[1])['items']} == {ident}
assert {i['id'] for i in json.loads(bob.request('Items')[1])['items']} == {other_id}
# Video route tests use a synthetic BMFF header, not a playable video fixture.
video = b'\x00\x00\x00\x18ftypmp42\x00\x00\x00\x00mp42isom' + bytes(128)
code, body, _ = alice.upload(video, 'clip.mp4'); assert code == 200
video_id = json.loads(body)['item']['id']
assert bob.request('Video/' + video_id)[0] == 404
code, body, _ = alice.request('Video/' + video_id, headers={'Range':'bytes=0-15'})
assert code == 206 and body == video[:16]
assert alice.upload(bytes(1048577), 'too-big.jpg')[0] == 413
for _ in range(20):
    listing = json.loads(alice.request('Items')[1])
    saved = next(i for i in listing['items'] if i['id'] == ident)
    if saved['preview'] == 'unavailable': break
    time.sleep(.5)
assert saved['preview'] == 'unavailable'
assert admin.request('Original/' + ident)[1] == PNG
# Tampered cookies must not become a valid guest identity.
for cookie in alice.jar:
    if cookie.name == 'NewlandBrownGuestDev': cookie.value = cookie.value[:-2] + 'xx'
assert alice.session()['invited'] is False
assert alice.request('Items')[0] == 403
code, archive, _ = admin.request('Export'); assert code == 200
with zipfile.ZipFile(io.BytesIO(archive)) as z:
    index = json.loads(z.read('upload-index.json'))
    assert len(index) == 3
    entry = next(n for n in z.namelist() if n.startswith(ident + '/'))
    assert z.read(entry) == PNG
    assert all('..' not in n.split('/') for n in z.namelist())
print('PASS: invitation + CSRF, owner isolation, same-name separation, admin restrictions, original SHA-256, idempotent retries, failed-preview retention, video range access, upload limit, cookie tampering and original ZIP export.')
