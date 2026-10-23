#!/usr/bin/env python3
"""TEST-ONLY host: public gallery, control authorization, private originals and rate limits."""
import base64, hashlib, http.cookiejar, json, os, pathlib, urllib.error, urllib.parse, urllib.request, uuid
BASE = 'http://127.0.0.1:5088'
PNG = base64.b64decode('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aD1sAAAAASUVORK5CYII=')
class Client:
    def __init__(self, admin=False):
        self.jar = http.cookiejar.CookieJar()
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.jar))
        self.csrf = None
        self.admin = admin
    def request(self, path, data=None, headers=None, csrf=True):
        h = dict(headers or {})
        if self.admin: h['X-Test-Admin'] = 'test-host-only'
        if isinstance(data, dict): data = json.dumps(data).encode(); h['Content-Type'] = 'application/json'
        if data is not None and csrf and self.csrf: h['X-Wedding-CSRF'] = self.csrf
        try:
            with self.opener.open(urllib.request.Request(BASE + path, data=data, headers=h), timeout=20) as r:
                return r.status, r.read(), r.headers
        except urllib.error.HTTPError as e: return e.code, e.read(), e.headers
    def session(self):
        status, body, _ = self.request('/Wedding/Session'); assert status == 200, (status, body)
        data = json.loads(body); self.csrf = data['csrf']; return data
    def upload(self, payload=PNG, filename='private-filename.png', name='Guest One', **kwargs):
        headers = {'Content-Type': 'application/octet-stream', 'X-File-Name':filename,
                   'X-Guest-Name':urllib.parse.quote(name), 'X-Share-Name':'true', 'X-Upload-ID':uuid.uuid4().hex}
        headers.update(kwargs.pop('headers', {}))
        return self.request('/Wedding/Upload', payload, headers, **kwargs)
    def items(self):
        status, body, _ = self.request('/Wedding/Items'); assert status == 200
        return json.loads(body)['items']

alice, bob, admin = Client(), Client(), Client(True)
status, body, headers = alice.request('/'); assert status == 200 and b'through your eyes' in body and not headers.get('Location')
assert alice.session()['publicAccess'] and bob.session()['invited']
assert all(c.value not in [b.value for b in bob.jar] for c in alice.jar if c.name == 'NewlandBrownGuestDev')
admin.session()
status, body, _ = alice.upload(); assert status == 200, (status, body)
item = json.loads(body)['item']; ident = item['id']
assert item['sha256'] == hashlib.sha256(PNG).hexdigest()
shared = bob.items(); assert len(shared) == 1 and shared[0]['name'] == 'Guest One'
assert all(k not in shared[0] for k in ['owner','requestId','sha256','bytes','notification'])
assert shared[0]['filename'] != 'private-filename.png'
assert bob.request('/Wedding/Original/' + ident)[0] in (401,403)
assert bob.request('/Wedding/Export')[0] in (401,403)
assert bob.request('/Wedding/AdminItems')[0] in (401,403)
assert bob.request('/Wedding/PublicSettings', {'uploadsOpen':False,'galleryOpen':False})[0] in (401,403)
assert alice.upload(csrf=False)[0] == 400
assert alice.upload(headers={'Origin':'https://evil.example'})[0] == 403
assert alice.upload(headers={'Sec-Fetch-Site':'cross-site'})[0] == 403
# Install a tiny viewing fixture to exercise route access separately from decoding.
folder = pathlib.Path(os.environ['TEST_ROOT']) / 'media' / ident
manifest = json.loads((folder/'upload.json').read_text()); manifest['Preview'] = 'ready'
(folder/'preview.webp').write_bytes(b'RIFF1234WEBP-test-viewing-copy')
(folder/'upload.json').write_text(json.dumps(manifest))
assert bob.request('/Wedding/Preview/' + ident)[0] == 200
assert admin.request('/Wedding/Visibility/' + ident, {'hidden':True})[0] == 200
assert bob.items() == [] and bob.request('/Wedding/Preview/' + ident)[0] == 404
assert admin.request('/Wedding/Original/' + ident)[1] == PNG
assert admin.request('/Wedding/Visibility/' + ident, {'hidden':False})[0] == 200
# A legacy name was never consented for the public gallery.
manifest = json.loads((folder/'upload.json').read_text()); manifest['ShareName'] = False
(folder/'upload.json').write_text(json.dumps(manifest))
assert bob.items()[0]['name'] == ''
video = b'\x00\x00\x00\x18ftypmp42\x00\x00\x00\x00mp42isom' + bytes(128)
status, body, _ = alice.upload(video, 'private-video.mp4'); assert status == 200
video_id = json.loads(body)['item']['id']
assert alice.request('/Wedding/Video/' + video_id)[0] == 404
assert bob.request('/Wedding/Video/' + video_id)[0] == 404
assert admin.request('/Wedding/Original/' + video_id)[1] == video
assert admin.request('/Wedding/PublicSettings', {'uploadsOpen':False,'galleryOpen':True})[0] == 200
assert alice.upload()[0] == 403
assert len(bob.items()) == 2
assert json.loads((pathlib.Path(os.environ['TEST_ROOT'])/'media'/'.public-settings.json').read_text())['UploadsOpen'] is False
assert admin.request('/Wedding/PublicSettings', {'uploadsOpen':True,'galleryOpen':False})[0] == 200
assert bob.items() == [] and bob.request('/Wedding/Preview/' + ident)[0] == 404
assert admin.request('/Wedding/PublicSettings', {'uploadsOpen':True,'galleryOpen':True})[0] == 200
# Browser budget is exhausted even after submissions were closed/reopened.
assert alice.upload()[0] == 429
assert bob.upload()[0] == 200
assert bob.upload()[0] == 200
fresh = Client(); fresh.session()
status, body, headers = fresh.upload(headers={'X-Forwarded-For':'203.0.113.77'})
assert status == 429 and int(headers['Retry-After']) > 0, (status, body)
assert len(bob.items()) == 4
print('PASS: root, automatic identities, shared gallery, metadata minimization, legacy-name privacy, CSRF/origin checks, admin-only originals/control, no original-video fallback, hide/close controls and IP/browser limits despite cookie reset/spoofed XFF.')
