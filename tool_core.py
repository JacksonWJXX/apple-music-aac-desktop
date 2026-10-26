"""Pure validation/naming helpers. No network or credential discovery."""
from __future__ import annotations
import os
import shutil
import uuid
import hashlib
from pathlib import Path
import re
import unicodedata
from urllib.parse import urlsplit, parse_qs


class ToolError(Exception):
    def __init__(self, category):
        self.category = category
        super().__init__(category)


def parse_song_url(value):
    try:
        url = urlsplit(value.strip())
        if url.scheme != 'https' or url.hostname != 'music.apple.com' or url.username or url.password or url.port or url.fragment:
            raise ValueError()
        parts = [p for p in url.path.split('/') if p]
        if len(parts) not in (3, 4) or not re.fullmatch('[a-z]{2}', parts[0]):
            raise ValueError()
        query = parse_qs(url.query, keep_blank_values=True)
        if parts[1] == 'song' and parts[-1].isdigit():
            song_id = parts[-1]
            if 'i' in query and query['i'] != [song_id]:
                raise ValueError()
        elif parts[1] == 'album' and parts[-1].isdigit() and len(query.get('i', [])) == 1 and query['i'][0].isdigit():
            song_id = query['i'][0]
        else:
            raise ValueError()
        if not 1 <= len(song_id) <= 20:
            raise ValueError()
        return song_id, 'https://music.apple.com/{}/song/{}'.format(parts[0], song_id)
    except (ValueError, IndexError):
        raise ToolError('invalid_url') from None


def safe_filename(value, fallback='歌曲', limit=100):
    text = unicodedata.normalize('NFC', str(value))
    text = re.sub(r'[<>:"/\\|?*\x00-\x1f\x7f\ud800-\udfff]', '_', text)
    text = re.sub(r'\s+', ' ', text).strip(' .')
    text = text.encode('utf-16-le')[:limit * 2].decode('utf-16-le', errors='ignore').rstrip(' .') or fallback
    if re.match(r'^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)', text, re.I):
        text = '_' + text
    return text


def _units(value):
    return len(value.encode('utf-16-le')) // 2


def _bounded_component(value, limit, fallback='歌曲'):
    # Device-name protection can prepend '_' after the first truncation.
    clean = safe_filename(value, fallback, limit)
    return clean.encode('utf-16-le')[:limit*2].decode('utf-16-le', errors='ignore').rstrip(' .') or '_'


def _collision_stem(title, label, budget, ordinal=None):
    tail = '' if ordinal is None else ' (' + str(ordinal) + ')'
    label = safe_filename(label, '_', 100)
    label_budget = budget - 3 - _units(tail) - 1
    if label_budget < 1:
        raise ToolError('path_too_long')
    if _units(label) > label_budget:
        digest = hashlib.sha256(label.encode('utf-8')).hexdigest()[:8]
        label = (_bounded_component(label, label_budget-9, '_')+'~'+digest
                 if label_budget > 9 else digest[:label_budget])
    suffix = ' - ' + label + tail
    return _bounded_component(title, budget-_units(suffix)) + suffix


def publish_audio(source, destination, title, artist, song_id, work_id=None, temporary_directory=None):
    """Copy across volumes, then atomically publish without replacing files."""
    source, destination = Path(source), Path(destination)
    destination.mkdir(parents=True, exist_ok=True)
    budget = min(145, 240-len(str(destination).encode('utf-16-le'))//2-1-4)
    if budget < 20: raise ToolError('path_too_long')
    title = _bounded_component(title,min(100,budget))
    stems = [title, _collision_stem(title,artist,budget),
             _collision_stem(title,str(song_id),budget)]
    identity = work_id if work_id and re.fullmatch('[0-9a-f]{32}', work_id) else uuid.uuid4().hex
    temporary = (Path(temporary_directory) if temporary_directory is not None else destination) / ('.applemusic-' + identity + '.part')
    owned = False
    try:
        with temporary.open('xb') as output, source.open('rb') as input_file:
            owned = True
            shutil.copyfileobj(input_file, output, 1024 * 1024)
            output.flush()
            os.fsync(output.fileno())
        for number in range(1000):
            stem = stems[number] if number < 3 else _collision_stem(title,str(song_id),budget,number-1)
            target = destination / (stem + '.m4a')
            try:
                if os.name == 'nt':
                    os.rename(temporary, target)
                else:
                    os.link(temporary, target)
            except FileExistsError:
                continue
            source.unlink()
            return target
        raise ToolError('collision_limit')
    except OSError:
        raise ToolError('save_failed') from None
    finally:
        if owned:
            temporary.unlink(missing_ok=True)
