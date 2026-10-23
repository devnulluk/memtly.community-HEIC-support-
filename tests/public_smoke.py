#!/usr/bin/env python3
"""TEST-ONLY: open uploads, owner-only default, explicit sharing, media access and limits."""
import base64, hashlib, http.cookiejar, json, os, pathlib, urllib.error, urllib.parse, urllib.request, uuid
BASE = 'http://127.0.0.1:5088'
SHARED = os.environ.get('TEST_SHARED') == 'true'
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
a_session, b_session = alice.session(), bob.session()
assert a_session['publicAccess'] and b_session['invited']
assert a_session['sharedGallery'] is SHARED
assert all(c.value not in [b.value for b in bob.jar] for c in alice.jar if c.name == 'NewlandBrownGuestDev')
admin.session()
status, body, _ = alice.upload(); assert status == 200, (status, body)
item = json.loads(body)['item']; ident = item['id']
assert item['sha256'] == hashlib.sha256(PNG).hexdigest()
assert len(alice.items()) == 1 and alice.items()[0]['name'] == 'Guest One'
assert len(bob.items()) == (1 if SHARED else 0)
visible = alice.items()[0]
assert all(k not in visible for k in ['owner','requestId','sha256','bytes','notification'])
assert visible['filename'] != 'private-filename.png'
assert bob.request('/Wedding/Original/' + ident)[0] in (401,403)
assert bob.request('/Wedding/Export')[0] in (401,403)
assert bob.request('/Wedding/AdminItems')[0] in (401,403)
assert bob.request('/Wedding/PublicSettings', {'uploadsOpen':False,'galleryOpen':False})[0] in (401,403)
assert alice.upload(csrf=False)[0] == 400
assert alice.upload(headers={'Origin':'https://evil.example'})[0] == 403
assert alice.upload(headers={'Sec-Fetch-Site':'cross-site'})[0] == 403
# The background test preview worker intentionally fails. Stop its retry path
# by installing a ready fixture, then exercise access independently of decoding.
folder = pathlib.Path(os.environ['TEST_ROOT']) / 'media' / ident
manifest = json.loads((folder/'upload.json').read_text()); manifest['Preview'] = 'ready'
(folder/'preview.webp').write_bytes(b'RIFF1234WEBP-test-viewing-copy')
(folder/'upload.json').write_text(json.dumps(manifest))
assert alice.request('/Wedding/Preview/' + ident)[0] == 200
assert bob.request('/Wedding/Preview/' + ident)[0] == (200 if SHARED else 404)
assert bob.request('/Wedding/Preview/' + ident, headers={'Range':'bytes=0-4'})[0] == (200 if SHARED else 404)
assert Client().request('/Wedding/Preview/' + ident)[0] == 404
if not SHARED:
    # A submitted owner/name parameter and old name-sharing consent never grant access.
    assert json.loads(bob.request('/Wedding/Items?owner=' + manifest['Owner'])[1])['total'] == 0
    assert bob.request('/Wedding/Preview/' + ident, headers={'X-Guest-Name':'Guest One','X-Owner':manifest['Owner']})[0] == 404
    manifest['ShareName'] = True; (folder/'upload.json').write_text(json.dumps(manifest))
    assert bob.items() == [] and bob.request('/Wedding/Preview/' + ident)[0] == 404
assert admin.request('/Wedding/Visibility/' + ident, {'hidden':True})[0] == 200
assert alice.items() == [] and bob.items() == []
assert alice.request('/Wedding/Preview/' + ident)[0] == 404
assert bob.request('/Wedding/Preview/' + ident)[0] == 404
assert admin.request('/Wedding/Original/' + ident)[1] == PNG
assert admin.request('/Wedding/Visibility/' + ident, {'hidden':False})[0] == 200
# Legacy names remain private in the explicitly shared mode.
manifest = json.loads((folder/'upload.json').read_text()); manifest['ShareName'] = False
(folder/'upload.json').write_text(json.dumps(manifest))
if SHARED: assert bob.items()[0]['name'] == ''
else: assert alice.items()[0]['name'] == 'Guest One' and bob.items() == []
video = b'\x00\x00\x00\x18ftypmp42\x00\x00\x00\x00mp42isom' + bytes(128)
status, body, _ = alice.upload(video, 'private-video.mp4'); assert status == 200
video_id = json.loads(body)['item']['id']
assert alice.request('/Wedding/Video/' + video_id)[0] == 404
assert bob.request('/Wedding/Video/' + video_id)[0] == 404
assert admin.request('/Wedding/Original/' + video_id)[1] == video
vf = pathlib.Path(os.environ['TEST_ROOT']) / 'media' / video_id
vm = json.loads((vf/'upload.json').read_text()); vm['VideoPreview'] = 'ready'
preview = b'separate-video-preview-fixture'
(vf/'preview.video.mp4').write_bytes(preview); (vf/'upload.json').write_text(json.dumps(vm))
assert alice.request('/Wedding/Video/' + video_id)[1] == preview
assert bob.request('/Wedding/Video/' + video_id)[0] == (200 if SHARED else 404)
assert bob.request('/Wedding/Video/' + video_id, headers={'Range':'bytes=0-3'})[0] == (206 if SHARED else 404)
assert Client().request('/Wedding/Video/' + video_id)[0] == 404
assert admin.request('/Wedding/PublicSettings', {'uploadsOpen':False,'galleryOpen':True})[0] == 200
assert alice.upload()[0] == 403
assert len(alice.items()) == 2 and len(bob.items()) == (2 if SHARED else 0)
assert json.loads((pathlib.Path(os.environ['TEST_ROOT'])/'media'/'.public-settings.json').read_text())['UploadsOpen'] is False
assert admin.request('/Wedding/PublicSettings', {'uploadsOpen':True,'galleryOpen':False})[0] == 200
assert alice.items() == [] and bob.items() == []
assert alice.request('/Wedding/Preview/' + ident)[0] == 404
assert alice.request('/Wedding/Video/' + video_id)[0] == 404
assert admin.request('/Wedding/Video/' + video_id)[1] == preview
assert admin.request('/Wedding/PublicSettings', {'uploadsOpen':True,'galleryOpen':True})[0] == 200
# Opening guest previews does not enable sharing, including for legacy uploads.
assert alice.session()['sharedGallery'] is SHARED
assert len(bob.items()) == (2 if SHARED else 0)
# Browser budget is exhausted even after submissions were closed/reopened.
assert alice.upload()[0] == 429
assert bob.upload()[0] == 200
assert bob.upload()[0] == 200
fresh = Client(); fresh.session()
status, body, headers = fresh.upload(headers={'X-Forwarded-For':'203.0.113.77'})
assert status == 429 and int(headers['Retry-After']) > 0, (status, body)
assert len(bob.items()) == (4 if SHARED else 2)
assert len(alice.items()) == (4 if SHARED else 2)
assert len(fresh.items()) == (4 if SHARED else 0)
assert json.loads(admin.request('/Wedding/AdminItems')[1])['total'] == 4
print('PASS: root, automatic distinct identities, owner-only default/explicit sharing, direct photo/video/range authorization, legacy ownership, metadata, CSRF, admin-only masters/controls, hidden/closed previews, and IP/browser rate limits. Shared:', SHARED)
