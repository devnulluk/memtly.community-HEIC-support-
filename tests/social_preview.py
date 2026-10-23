#!/usr/bin/env python3
"""Validate the actual initial HTML and static JPEG without JS, login or cookies.

Use --base-url/--ca-file for the isolated HTTPS production host or the live site.
No media is uploaded and no private collection is read by this test.
"""
import argparse
import hashlib
from html.parser import HTMLParser
from pathlib import Path
import ssl
import struct
import urllib.error
import urllib.parse
import urllib.request
import urllib.robotparser

PUBLIC_URL = 'https://wedding-photos.newland-brown.com/'
IMAGE_PATH = '/wedding/assets/social-preview-v1.jpg'
TITLE = 'Mark & Keziah — Wedding Photos'
DESCRIPTION = 'Share your memories. Upload your photos and videos from Mark & Keziah’s wedding. No account needed.'
ROOT = Path(__file__).resolve().parents[1]
WEBROOT = ROOT / 'Memtly.Community/wwwroot'
USER_AGENTS = (
    'facebookexternalhit/1.1',
    'WhatsApp/2.0',
    'Twitterbot/1.0',
    'TelegramBot (like TwitterBot)',
    'Discordbot/2.0',
)


class Head(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.inside = False
        self.title_open = False
        self.title = ''
        self.meta = {}
        self.canonicals = []

    def handle_starttag(self, tag, attributes):
        attrs = dict(attributes)
        if tag == 'head':
            self.inside = True
        if not self.inside:
            return
        if tag == 'title':
            self.title_open = True
        if tag == 'meta':
            key = attrs.get('property') or attrs.get('name')
            if key:
                assert key not in self.meta, f'Duplicate metadata: {key}'
                self.meta[key] = attrs.get('content', '')
        if tag == 'link' and attrs.get('rel') == 'canonical':
            self.canonicals.append(attrs.get('href'))

    def handle_endtag(self, tag):
        if tag == 'head':
            self.inside = False
        if tag == 'title':
            self.title_open = False

    def handle_data(self, text):
        if self.inside and self.title_open:
            self.title += text


def check_html(body):
    assert body.lower().find(b'</head>') in range(8192), 'Metadata must be in the first 8 KiB'
    head = Head()
    head.feed(body.decode('utf-8'))
    assert head.title == TITLE
    assert head.canonicals == [PUBLIC_URL]
    expected = {
        'description': DESCRIPTION,
        'og:type': 'website', 'og:locale': 'en_GB', 'og:site_name': 'Mark & Keziah',
        'og:title': TITLE, 'og:description': DESCRIPTION, 'og:url': PUBLIC_URL,
        'og:image': PUBLIC_URL.rstrip('/') + IMAGE_PATH,
        'og:image:secure_url': PUBLIC_URL.rstrip('/') + IMAGE_PATH,
        'og:image:type': 'image/jpeg', 'og:image:width': '1200', 'og:image:height': '630',
        'twitter:card': 'summary_large_image', 'twitter:title': TITLE,
        'twitter:description': DESCRIPTION, 'twitter:image': PUBLIC_URL.rstrip('/') + IMAGE_PATH,
    }
    for key, value in expected.items():
        assert head.meta.get(key) == value, f'{key}: {head.meta.get(key)!r}'
    assert 'cherub' in head.meta.get('og:image:alt', '').lower()
    assert head.meta.get('twitter:image:alt') == head.meta['og:image:alt']
    assert 'noindex' in head.meta.get('robots', '')
    assert not any('/Wedding/Preview/' in value or '#invite' in value for value in head.meta.values())


def jpeg_size(data):
    assert data[:2] == b'\xff\xd8' and data[-2:] == b'\xff\xd9', 'Not a complete JPEG'
    position = 2
    while position + 4 <= len(data):
        assert data[position] == 255, 'Invalid JPEG marker'
        while data[position] == 255:
            position += 1
        marker = data[position]
        position += 1
        if marker in (0xD8, 0xD9) or 0xD0 <= marker <= 0xD7:
            continue
        length = struct.unpack_from('>H', data, position)[0]
        assert length >= 2
        if marker in (0xC0, 0xC1, 0xC2):
            depth, height, width = struct.unpack_from('>BHH', data, position + 2)
            assert depth == 8
            return width, height
        if marker == 0xDA:
            break
        position += length
    raise AssertionError('No JPEG frame dimensions found')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--base-url')
    parser.add_argument('--ca-file')
    args = parser.parse_args()
    expected_image = (WEBROOT / IMAGE_PATH.lstrip('/')).read_bytes()
    assert 10000 < len(expected_image) < 1000000
    assert jpeg_size(expected_image) == (1200, 630)
    check_html((WEBROOT / 'wedding/index.html').read_bytes())
    if not args.base_url:
        print('PASS: static Open Graph/Twitter metadata and 1200x630 JPEG.')
        return
    base = args.base_url.rstrip('/')
    assert urllib.parse.urlparse(base).scheme == 'https', 'Test must verify HTTPS'
    tls = ssl.create_default_context(cafile=args.ca_file)

    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, req, fp, code, msg, headers, newurl):
            return None

    opener = urllib.request.build_opener(
        urllib.request.ProxyHandler({}), urllib.request.HTTPSHandler(context=tls), NoRedirect())

    def request(path, user_agent, method='GET'):
        req = urllib.request.Request(base + path, method=method, headers={
            'User-Agent': user_agent, 'Accept-Encoding': 'identity'})
        try:
            with opener.open(req, timeout=20) as response:
                body = response.read(1000001)
                assert len(body) <= 1000000
                return response.status, response.headers, body
        except urllib.error.HTTPError as error:
            return error.code, error.headers, error.read(1000001)

    for user_agent in USER_AGENTS:
        for path in ('/', '/Wedding', '/?share=1'):
            status, headers, body = request(path, user_agent)
            assert status == 200, f'{user_agent} {path}: HTTP {status}'
            assert 'text/html' in headers.get('Content-Type', '')
            assert not headers.get('Location')
            check_html(body)
        status, headers, body = request(IMAGE_PATH, user_agent)
        assert status == 200 and headers.get_content_type() == 'image/jpeg'
        assert body == expected_image, 'Served image differs from the committed public artwork'
        status, headers, body = request(IMAGE_PATH, user_agent, 'HEAD')
        assert status == 200 and headers.get_content_type() == 'image/jpeg' and not body
        # A bot user agent must not bypass existing administrator authentication.
        for path in ('/Wedding/Admin', '/Wedding/Export'):
            status, headers, body = request(path, user_agent)
            assert status in (302, 401, 403), f'Protected endpoint accessible to {user_agent}: {path}'
        status, headers, body = request('/robots.txt', user_agent)
        if status == 200:
            robots = urllib.robotparser.RobotFileParser()
            robots.parse(body.decode('utf-8').splitlines())
            assert robots.can_fetch(user_agent, base + '/')
            assert robots.can_fetch(user_agent, base + IMAGE_PATH)
        else:
            assert status == 404, f'Unexpected robots response: {status}'
    print('PASS: HTTPS cookie-free initial HTML and JPEG GET/HEAD for five social crawler user agents; protected admin/export unchanged.')
    print('Preview JPEG SHA256:', hashlib.sha256(expected_image).hexdigest())


if __name__ == '__main__':
    main()
