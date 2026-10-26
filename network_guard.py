"""Apple-only request guard for the pinned downloader stack; never logs URLs."""
from urllib.parse import urlsplit
import urllib.request

STATE = {'category': None, 'http_status': None}
AUTH_HOSTS = {'music.apple.com', 'amp-api.music.apple.com', 'play.itunes.apple.com'}
ACCOUNT_HEADERS = {'authorization', 'proxy-authorization', 'cookie', 'cookie2',
                   'media-user-token', 'music-user-token', 'x-media-user-token',
                   'x-apple-dsid', 'x-dsid', 'dsid', 'x-apple-adsid'}
PUBLIC_HEADERS = {'accept', 'accept-encoding', 'accept-language', 'user-agent', 'range',
                  'if-range', 'if-none-match', 'if-modified-since', 'connection', 'host',
                  'origin', 'referer', 'content-length', 'content-type', 'pragma',
                  'cache-control', 'dnt', 'upgrade-insecure-requests', 'te', 'priority',
                  'sec-fetch-dest', 'sec-fetch-mode', 'sec-fetch-site', 'sec-fetch-user',
                  'sec-ch-ua', 'sec-ch-ua-mobile', 'sec-ch-ua-platform'}


def account_header(name):
    return name in ACCOUNT_HEADERS or any(marker in name for marker in
        ('token', 'auth', 'cookie', 'session', 'dsid', 'credential', 'password', 'secret', 'api-key', 'account'))


def blocked():
    STATE['category'] = 'blocked_destination'
    raise RuntimeError('DESTINATION_BLOCKED')


def check_request(url, headers=None):
    try:
        parts = urlsplit(str(url))
        port = parts.port
    except ValueError:
        blocked()
    host = (parts.hostname or '').lower()
    allowed = any(host == suffix or host.endswith('.' + suffix)
                  for suffix in ('apple.com', 'mzstatic.com', 'cdn-apple.com'))
    names = [(k.decode('ascii', errors='ignore') if isinstance(k, bytes) else str(k)).lower()
             for k, value in (headers or {}).items() if value]
    if parts.scheme != 'https' or port not in (None, 443) or parts.netloc.endswith(':') or parts.username or parts.password or not allowed:
        blocked()
    if host not in AUTH_HOSTS:
        # Positive header allowlist on media/CDN hosts also blocks unknown account
        # header spellings, rather than assuming only Cookie/Authorization carry auth.
        if any(account_header(name) or name not in PUBLIC_HEADERS for name in names):
            blocked()


def record(response):
    status = getattr(response, 'status_code', None)
    if status and status >= 400:
        STATE['http_status'] = status
        STATE['category'] = 'authentication' if status in (401, 403) else 'apple_response'
    return response


def install():
    import httpx
    import requests
    original_async = httpx.AsyncClient._send_single_request
    original_sync = httpx.Client._send_single_request
    original_requests = requests.Session.send
    original_open = urllib.request.OpenerDirector.open

    async def send_async(self, request):
        check_request(request.url, request.headers)
        try:
            return record(await original_async(self, request))
        except httpx.TransportError:
            STATE['category'] = 'network'
            raise

    def send_sync(self, request):
        check_request(request.url, request.headers)
        try:
            return record(original_sync(self, request))
        except httpx.TransportError:
            STATE['category'] = 'network'
            raise

    def send_requests(self, request, **kwargs):
        check_request(request.url, request.headers)
        return record(original_requests(self, request, **kwargs))

    def open_url(self, fullurl, data=None, timeout=None):
        if isinstance(fullurl, urllib.request.Request):
            check_request(fullurl.full_url, dict(fullurl.header_items()))
        else:
            check_request(fullurl)
        if timeout is None:
            return original_open(self, fullurl, data)
        return original_open(self, fullurl, data, timeout)

    httpx.AsyncClient._send_single_request = send_async
    httpx.Client._send_single_request = send_sync
    requests.Session.send = send_requests
    urllib.request.OpenerDirector.open = open_url
