"""Real yt-dlp transfer counters only; never forward URLs, paths or raw messages."""
from __future__ import annotations
from contextlib import contextmanager
import math
import threading
import time


def counter(value):
    try:
        value = float(value)
        return int(value) if math.isfinite(value) and value >= 0 else None
    except (TypeError, ValueError, OverflowError):
        return None


class TransferProgress:
    def __init__(self, emit, interval=.2):
        self.emit, self.interval = emit, interval
        self.lock = threading.Lock()
        self.last = 0.0
        self.hls = False

    def hook(self, record, kind='http'):
        with self.lock:
            if kind == 'hls':
                self.hls = True
            elif self.hls:
                return
            if record.get('status') == 'finished':
                self.emit('processing')
                return
            if record.get('status') != 'downloading':
                return
            now = time.monotonic()
            if now-self.last < self.interval:
                return
            self.last = now
            done = counter(record.get('downloaded_bytes'))
            total = counter(record.get('total_bytes'))
            estimate = counter(record.get('total_bytes_estimate'))
            fragment = counter(record.get('fragment_index'))
            fragments = counter(record.get('fragment_count'))
            fields = {}
            if done is not None:
                fields['bytes_done'] = done
            percent = None
            if total and done is not None:
                fields['bytes_total'] = total
                percent = done/total*100
            elif fragments and fragment is not None:
                fields.update(fragments_done=fragment, fragments_total=fragments)
                percent = fragment/fragments*100
            elif estimate and done is not None:
                fields.update(bytes_total=estimate, estimated=True)
                percent = done/estimate*100
            if percent is not None:
                # Transfer completion is followed by mux/decrypt/verification.
                # Never expose 100% as a completed-file claim before verification.
                if percent >= 100:
                    self.emit('processing', **fields)
                    return
                fields['percent'] = min(99.9, round(max(0.0, percent), 1))
            self.emit('downloading', **fields)


@contextmanager
def downloader_progress(reporter):
    import yt_dlp.downloader as selector
    from yt_dlp.downloader import http, hls
    original_http, original_hls = http.HttpFD, hls.HlsFD

    class ProgressHttp(original_http):
        def __init__(self, *args, **kwargs):
            super().__init__(*args, **kwargs)
            self.add_progress_hook(lambda data: reporter.hook(data, 'http'))

    class ProgressHls(original_hls):
        def __init__(self, *args, **kwargs):
            super().__init__(*args, **kwargs)
            self.add_progress_hook(lambda data: reporter.hook(data, 'hls'))

    # The pinned selector caches these classes at import time, independently
    # of the concrete submodules used by the upstream in-process AAC path.
    replacements = {original_http: ProgressHttp, original_hls: ProgressHls}
    aliases = [(owner, name, getattr(owner, name))
               for owner, name in ((http, 'HttpFD'), (hls, 'HlsFD'),
                                   (selector, 'HttpFD'), (selector, 'HlsFD'))]
    protocols = {key: value for key, value in selector.PROTOCOL_MAP.items()
                 if value in replacements}
    for owner, name, original in aliases:
        setattr(owner, name, replacements[original])
    for key, original in protocols.items():
        selector.PROTOCOL_MAP[key] = replacements[original]
    try:
        yield
    finally:
        for owner, name, original in aliases:
            setattr(owner, name, original)
        for key, original in protocols.items():
            selector.PROTOCOL_MAP[key] = original
