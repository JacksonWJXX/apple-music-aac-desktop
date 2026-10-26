"""Local album paths, offline planning and journaled non-overwriting moves.

No account/network modules. Private plans/journals must never be published.
"""
from __future__ import annotations
import contextlib
import errno
import hashlib
import json
import math
import os
from pathlib import Path, PurePosixPath
import stat
import subprocess
import tempfile
import time
import unicodedata
import uuid
from tool_core import ToolError, safe_filename

SUPPORTED = {'.m4a', '.mp3', '.flac', '.wav', '.ogg', '.opus', '.aif', '.aiff', '.aac', '.wma'}
RECORDS = '.applemusic-organizer'
MAX_FILES, MAX_DIRS, MAX_DEPTH = 5000, 2000, 8


def units(text):
    return len(str(text).encode('utf-16-le', errors='surrogatepass')) // 2


def checked_path(path, *, exists=True):
    """Check lexical ancestors before resolving anything; never follow a link."""
    path = Path(path)
    if not path.is_absolute() or '..' in path.parts or str(path).startswith('\\\\'):
        raise ToolError('unsafe_path')
    for part in reversed((path, *path.parents)):
        try:
            info = part.lstat()
        except FileNotFoundError:
            if part == path and exists:
                raise ToolError('missing_path') from None
            continue
        if stat.S_ISLNK(info.st_mode) or getattr(info, 'st_file_attributes', 0) & 0x400:
            raise ToolError('reparse_path')
    return path


def library_root(value, *, create=False):
    root = checked_path(value, exists=not create)
    if root.parent == root or units(root) > 180:
        raise ToolError('root_not_supported')
    if create:
        root.mkdir(parents=True, exist_ok=True)
    checked_path(root)
    if (root/'tool-config.json').is_file() or (root/'.git').exists():
        raise ToolError('not_music_folder')
    if not root.is_dir():
        raise ToolError('root_not_supported')
    return root


def album_name(value):
    if not isinstance(value, str) or not value.strip():
        return '未知专辑'
    text = unicodedata.normalize('NFC', value.strip())
    return text if len(text) <= 256 else text[:223] + '~' + hashlib.sha256(text.encode('utf-8',errors='surrogatepass')).hexdigest()[:16]


def album_component(value, root):
    raw = album_name(value)
    limit = min(80, 240 - units(root) - 1 - 1 - 40)
    if limit < 18:
        raise ToolError('root_not_supported')
    clean = safe_filename(raw, '未知专辑', limit)
    if clean != raw:
        digest = hashlib.sha256(raw.casefold().encode('utf-8', errors='surrogatepass')).hexdigest()[:12]
        clean = safe_filename(raw, '专辑', limit - 13) + '~' + digest
    return clean


def album_directory(root, album, *, create=False):
    root = library_root(root)
    target = checked_path(root / album_component(album, root), exists=False)
    if target.exists() and not target.is_dir():
        raise ToolError('album_folder_blocked')
    if create:
        target.mkdir(exist_ok=True)
        checked_path(target)
    return target


def track_name(stem, suffix, directory, number=1):
    limit = min(145, 240 - units(directory) - 1 - units(suffix))
    tail = '' if number == 1 else ' (' + str(number) + ')'
    if limit - len(tail) < 12:
        raise ToolError('path_too_long')
    return safe_filename(stem, '歌曲', limit-len(tail)) + tail + suffix.lower()


@contextlib.contextmanager
def library_lock(root):
    root = library_root(root)
    path = checked_path(root / '.applemusic-library.lock', exists=False)
    stream = None
    locked = False
    try:
        stream = path.open('a+b')
        if not stat.S_ISREG(os.fstat(stream.fileno()).st_mode):
            raise ToolError('unsafe_path')
        if stream.seek(0, os.SEEK_END) == 0:
            stream.write(b'\0'); stream.flush()
        stream.seek(0)
        try:
            if os.name == 'nt':
                import msvcrt
                msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(stream.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
            locked = True
        except OSError:
            raise ToolError('library_busy') from None
        yield root
    finally:
        if stream:
            if locked:
                stream.seek(0)
                if os.name == 'nt':
                    import msvcrt
                    msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)
                else:
                    import fcntl
                    fcntl.flock(stream.fileno(), fcntl.LOCK_UN)
            stream.close()


def audio_album(path, ffprobe, spool_directory=None):
    """Only whitelisted local demuxers; no playlists or network protocols."""
    if path.suffix.lower() not in SUPPORTED:
        raise ToolError('unsupported_audio')
    args = [str(ffprobe), '-v', 'error', '-protocol_whitelist', 'file',
            '-format_whitelist', 'mov,mp3,flac,wav,ogg,aiff,aac,asf',
            '-show_entries', 'stream=codec_type:stream_disposition=attached_pic:format_tags=album:stream_tags=album',
            '-of', 'json', str(path)]
    try:
        with tempfile.TemporaryFile(dir=spool_directory or path.parent) as output:
            result = subprocess.run(args, stdout=output, stderr=subprocess.DEVNULL,
                                    creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0), timeout=15)
            if result.returncode or output.tell() > 262144:
                raise ToolError('invalid_audio')
            output.seek(0); info = json.load(output)
        streams = info.get('streams', [])
        if not any(s.get('codec_type') == 'audio' for s in streams):
            raise ToolError('invalid_audio')
        if any(s.get('codec_type') == 'video' and not s.get('disposition', {}).get('attached_pic') for s in streams):
            raise ToolError('not_music_audio')
        for tags in [info.get('format', {}).get('tags', {}), *(s.get('tags', {}) for s in streams)]:
            for key, value in tags.items():
                if key.casefold() == 'album' and isinstance(value, str) and value.strip():
                    return album_name(value)
        return album_name(None)
    except (OSError, ValueError, subprocess.TimeoutExpired):
        raise ToolError('audio_read_failed') from None


def stamp(path):
    info = checked_path(path).lstat()
    if not stat.S_ISREG(info.st_mode) or info.st_nlink > 1:
        raise ToolError('not_regular_audio')
    return [info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns]


def fingerprint(path, cancel):
    before = stamp(path)
    if before[2] > 2 * 1024**3:
        raise ToolError('audio_too_large')
    digest = hashlib.sha256()
    with path.open('rb') as source:
        while chunk := source.read(1024 * 1024):
            if cancel():
                raise ToolError('cancelled')
            digest.update(chunk)
    if stamp(path) != before:
        raise ToolError('changed_since_plan')
    return {'stat': before, 'sha256': digest.hexdigest()}


def relative(root, value):
    if not isinstance(value, str) or '\\' in value or ':' in value:
        raise ToolError('unsafe_plan')
    path = PurePosixPath(value)
    if path.is_absolute() or any(p in ('', '.', '..') for p in path.parts):
        raise ToolError('unsafe_plan')
    target = checked_path(root.joinpath(*path.parts), exists=False)
    if not target.is_relative_to(root) or target == root:
        raise ToolError('unsafe_plan')
    return target


def make_plan(root, ffprobe, cancel=lambda: False, progress=lambda **fields: None, spool_directory=None):
    root = library_root(root)
    entries, files, directories = [], 0, 0
    candidates = []
    def walk(directory, depth):
        nonlocal files, directories
        if cancel(): raise ToolError('cancelled')
        directories += 1
        if depth > MAX_DEPTH or directories > MAX_DIRS:
            raise ToolError('scan_limit')
        for item in sorted(os.scandir(directory), key=lambda e: e.name.casefold()):
            if cancel(): raise ToolError('cancelled')
            path = Path(item.path)
            if item.name.startswith('.'):
                continue
            try:
                if not item.is_dir(follow_symlinks=False) and path.suffix.lower() not in SUPPORTED:
                    continue
                if len(entries) >= MAX_FILES+MAX_DIRS: raise ToolError('scan_limit')
                checked_path(path)
                if item.is_dir(follow_symlinks=False):
                    if (path/'tool-config.json').is_file() or (path/'.git').exists():
                        continue
                    walk(path, depth+1)
                elif path.suffix.lower() in SUPPORTED:
                    files += 1
                    if files > MAX_FILES: raise ToolError('scan_limit')
                    candidates.append(path)
            except (OSError, ToolError) as error:
                if isinstance(error, ToolError) and error.category in ('cancelled','scan_limit'):
                    raise
                entries.append({'source':path.relative_to(root).as_posix(), 'target':'', 'action':'skip',
                                'reason':error.category if isinstance(error, ToolError) else 'access_denied'})
    walk(root, 0)
    reserved = set()
    for index, path in enumerate(candidates):
        if cancel(): raise ToolError('cancelled')
        progress(done=index, total=len(candidates))
        source = path.relative_to(root).as_posix()
        try:
            checked_path(path); album = audio_album(path, ffprobe, spool_directory or root)
            directory = album_directory(root, album)
            target = directory / track_name(path.stem, path.suffix, directory)
            if os.path.normcase(str(path)) == os.path.normcase(str(target)):
                entries.append({'source':source, 'target':source, 'album':album, 'action':'keep', 'reason':'already_archived'})
                reserved.add(os.path.normcase(str(target))); continue
            for number in range(1,1001):
                target = directory / track_name(path.stem, path.suffix, directory, number)
                if not target.exists() and os.path.normcase(str(target)) not in reserved:
                    break
            else: raise ToolError('collision_limit')
            identity = fingerprint(path, cancel)
            reserved.add(os.path.normcase(str(target)))
            entries.append({'source':source, 'target':target.relative_to(root).as_posix(), 'album':album,
                            'action':'move', 'reason':'', 'identity':identity})
        except (OSError, ToolError) as error:
            if isinstance(error, ToolError) and error.category == 'cancelled': raise
            entries.append({'source':source, 'target':'', 'action':'skip',
                            'reason':error.category if isinstance(error, ToolError) else 'access_denied'})
    info = root.stat()
    return {'version':1, 'root_id':[info.st_dev,info.st_ino], 'entries':entries,
            'moves':sum(e['action']=='move' for e in entries),
            'already':sum(e['action']=='keep' for e in entries),
            'skipped':sum(e['action']=='skip' for e in entries)}


def rename_no_replace(source, target):
    if os.name != 'nt':
        # This product supports Windows. Never use POSIX rename's overwrite
        # semantics for an existing-library operation.
        raise ToolError('platform_not_supported')
    os.rename(source, target)  # Windows fails if target exists or is cross-volume.


def execute_plan(root, plan, cancel=lambda: False, progress=lambda **fields: None):
    root = library_root(root)
    info = root.stat()
    if plan.get('version') != 1 or plan.get('root_id') != [info.st_dev,info.st_ino]:
        raise ToolError('changed_root')
    entries = plan.get('entries', [])
    if not isinstance(entries, list) or len(entries) > MAX_FILES+MAX_DIRS:
        raise ToolError('unsafe_plan')
    # Validate the entire proposed path set before any directory or move.
    moves = []
    for entry in entries:
        if entry.get('action') != 'move': continue
        source, target = relative(root, entry['source']), relative(root, entry['target'])
        if source.suffix.lower() not in SUPPORTED or target.suffix.lower() != source.suffix.lower():
            raise ToolError('unsafe_plan')
        if target.parent != root/album_component(entry.get('album'),root):
            raise ToolError('unsafe_plan')
        moves.append((entry,source,target))
    result = {'moved':0,'failed':0,'already':plan.get('already',0),'skipped':plan.get('skipped',0),
              'cancelled':False,'journal':'','fatal':''}
    if cancel(): result['cancelled']=True;return result
    records = checked_path(root/RECORDS, exists=False);records.mkdir(exist_ok=True);checked_path(records)
    journal = records/(time.strftime('%y%m%d-%H%M%S')+'-'+uuid.uuid4().hex[:12]+'.jsonl')
    result['journal'] = journal.relative_to(root).as_posix()
    with journal.open('x',encoding='utf-8') as log:
        def event(kind, **fields):
            log.write(json.dumps({'event':kind,'utc':time.time(),**fields},ensure_ascii=True)+'\n');log.flush();os.fsync(log.fileno())
        event('start',version=1,total=len(moves))
        for index,(entry,source,target) in enumerate(moves):
            if cancel(): result['cancelled']=True;break
            intent=False
            try:
                library_root(root);checked_path(source)
                if fingerprint(source,cancel) != entry.get('identity'): raise ToolError('changed_since_plan')
                directory=album_directory(root,entry.get('album'),create=True)
                if directory!=target.parent: raise ToolError('unsafe_plan')
                checked_path(target,exists=False)
                if target.exists(): raise ToolError('target_changed')
                event('intent',source=entry['source'],target=entry['target'],identity=entry['identity']);intent=True
                checked_path(source);checked_path(target.parent)
                rename_no_replace(source,target)
                result['moved']+=1
                try:event('moved',source=entry['source'],target=entry['target'])
                except OSError:result['fatal']='journal_after_move';break
            except (OSError,ToolError) as error:
                if isinstance(error,ToolError) and error.category=='cancelled':result['cancelled']=True;break
                reason=error.category if isinstance(error,ToolError) else ('cross_volume' if error.errno==errno.EXDEV else 'file_busy_or_access')
                result['failed']+=1
                try:event('failed',source=entry['source'],target=entry['target'],reason=reason,intent=intent)
                except OSError:result['fatal']='journal_write';break
            progress(done=index+1,total=len(moves),moved=result['moved'],failed=result['failed'])
        try:event('end',**result)
        except OSError:result['fatal']='journal_write'
    return result
