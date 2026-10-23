#!/usr/bin/env python3
"""Stream past the former 500 MiB and inherited Int32 request limits.
Synthetic MP4-shaped bytes test size/integrity, NOT video playback or iPhone codecs.
No multi-gigabyte in-memory buffer or personal media is used.
"""
import hashlib, http.client, http.cookiejar, json, urllib.request, uuid
BASE = 'http://127.0.0.1:5089'
TOKEN = 'ci-only-production-image-guest-invitation-12345678'
jar = http.cookiejar.CookieJar()
opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar))
def get(path):
    with opener.open(BASE + path, timeout=30) as r:
        assert r.status == 200
        return json.load(r)
state = get('/Wedding/Session')
assert state['maxFileBytes'] == 0, state
with opener.open(urllib.request.Request(BASE + '/Wedding/Enter',
    data=json.dumps({'token': TOKEN}).encode(), headers={
        'Content-Type': 'application/json', 'X-Wedding-CSRF': state['csrf']}), timeout=30) as r:
    assert r.status == 200
state = get('/Wedding/Session')
headers = {'Cookie': '; '.join(c.name + '=' + c.value for c in jar),
           'X-Wedding-CSRF':state['csrf'], 'Content-Type':'application/octet-stream',
           'X-Guest-Name':'Large%20upload%20CI'}
head = b'\x00\x00\x00\x18ftypmp42\x00\x00\x00\x00mp42isom'
length = 2 * 1024**3 + 1024**2  # 2 GiB + 1 MiB, actually transmitted and hashed.
conn = http.client.HTTPConnection('127.0.0.1', 5089, timeout=180)
conn.putrequest('POST', '/Wedding/Upload')
for key, value in {**headers, 'Content-Length':str(length), 'X-File-Size':str(length),
                   'X-File-Name':'large-boundary.mp4', 'X-Upload-ID':uuid.uuid4().hex}.items():
    conn.putheader(key, value)
conn.endheaders()
conn.send(head)
digest = hashlib.sha256(head)
block = bytes(1024**2)
remaining = length - len(head)
while remaining:
    part = memoryview(block)[:min(remaining, len(block))]
    conn.send(part); digest.update(part); remaining -= len(part)
response = conn.getresponse()
body = response.read()
assert response.status == 200, (response.status, body[:1000])
item = json.loads(body)['item']
assert item['bytes'] == length and item['sha256'] == digest.hexdigest(), item
conn.close()
# Chunked HTTP transports can declare the known File.size separately.
small = head + bytes(128)
conn = http.client.HTTPConnection('127.0.0.1', 5089, timeout=30)
conn.request('POST', '/Wedding/Upload', body=iter([small[:24], small[24:]]),
    headers={**headers, 'X-File-Size':str(len(small)), 'X-File-Name':'chunked.mov', 'X-Upload-ID':uuid.uuid4().hex}, encode_chunked=True)
response = conn.getresponse(); body = response.read()
assert response.status == 200, (response.status, body[:1000])
assert json.loads(body)['item']['sha256'] == hashlib.sha256(small).hexdigest()
conn.close()
conn = http.client.HTTPConnection('127.0.0.1', 5089, timeout=30)
conn.request('POST', '/Wedding/Upload', body=iter([small]),
    headers={**headers, 'X-File-Name':'undeclared.mov', 'X-Upload-ID':uuid.uuid4().hex}, encode_chunked=True)
response = conn.getresponse(); body = response.read()
assert response.status == 411, (response.status, body[:1000])
conn.close()
assert get('/Wedding/Items')['total'] == 2
print('PASS: streamed 2 GiB + 1 MiB through the real application with matching SHA-256, zero quota, declared-size chunked upload and rejection of undeclared-size uploads.')
