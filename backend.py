"""One isolated GUI job. Emits only allowlisted status data; stdout/stderr discarded."""
from __future__ import annotations
import asyncio
import http.cookiejar
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import threading

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT))
from tool_core import ToolError, parse_song_url, publish_audio, safe_filename
from transfer_progress import TransferProgress, downloader_progress

STATUS_LOCK = threading.Lock()


def status(job, phase, **safe_fields):
    value = {'phase': phase}
    for key in ('title', 'artist', 'category', 'file', 'duration', 'bytes', 'codec',
                'percent', 'bytes_done', 'bytes_total', 'fragments_done', 'fragments_total', 'estimated'):
        if key in safe_fields:
            value[key] = safe_fields[key]
    with STATUS_LOCK:
        temporary = job / 'status.new'
        temporary.write_text(json.dumps(value, ensure_ascii=True), encoding='utf-8')
        temporary.replace(job / 'status.json')


def validate_cookie(path):
    try:
        if path.is_symlink() or not path.is_file() or not 0 < path.stat().st_size < 1048576:
            raise ValueError()
        jar = http.cookiejar.MozillaCookieJar(str(path))
        jar.load(ignore_discard=True, ignore_expires=True)
        values = list(jar)
        if len(values) != 1 or values[0].name != 'media-user-token' or values[0].domain != '.music.apple.com' or not values[0].value or values[0].value == 'LOCAL_VALUE_HERE':
            raise ValueError()
    except Exception:
        raise ToolError('cookie_invalid') from None


def verify_audio(path, ffmpeg, ffprobe, expected_duration):
    creation = getattr(subprocess, 'CREATE_NO_WINDOW', 0)
    result = subprocess.run([ffprobe, '-v', 'error', '-select_streams', 'a:0',
                             '-show_entries', 'stream=codec_name,duration:format=duration',
                             '-of', 'json', str(path)], stdout=subprocess.PIPE,
                            stderr=subprocess.DEVNULL, creationflags=creation, timeout=90)
    try:
        info = json.loads(result.stdout)
        stream = info['streams'][0]
        duration = float(stream.get('duration', info.get('format', {}).get('duration', 0)))
        if result.returncode or stream['codec_name'] != 'aac' or duration <= 0:
            raise ValueError()
        if not expected_duration or abs(duration - expected_duration) > max(2.0, expected_duration * .01):
            raise ValueError()
    except (ValueError, KeyError, IndexError):
        raise ToolError('audio_incomplete') from None
    decode = subprocess.run([ffmpeg, '-hide_banner', '-nostdin', '-v', 'error',
                             '-i', str(path), '-map', '0:a:0', '-f', 'null', '-'],
                            stdout=subprocess.DEVNULL, stderr=subprocess.PIPE,
                            creationflags=creation, timeout=300)
    if decode.returncode or decode.stderr.strip():
        raise ToolError('audio_invalid')
    return duration


async def real_download(job, canonical, song_id, ffmpeg):
    import network_guard
    network_guard.install()
    import amdl.core_downloader as core
    from gamdl.interface import SongCodec
    from gamdl.downloader import DownloadMode
    original = core.AppleMusicDownloader
    metadata = {}
    staging = job / 'staging'
    complete = set()

    class SingleSongDownloader(original):
        async def get_download_item_from_url(self, url):
            async for item in super().get_download_item_from_url(url):
                media = item.media
                if str(media.media_id) != song_id:
                    raise ToolError('unexpected_media')
                data = media.media_metadata or {}
                if data.get('type') not in (None, 'songs'):
                    raise ToolError('unexpected_media')
                if item.final_path and not Path(item.final_path).resolve().is_relative_to(staging.resolve()):
                    raise ToolError('unexpected_media')
                if not media.partial and not media.error:
                    if complete:
                        raise ToolError('unexpected_media')
                    complete.add(song_id)
                    attributes = data.get('attributes', {})
                    title = attributes.get('name')
                    if not title or str(data.get('id')) != song_id:
                        raise ToolError('metadata')
                    metadata.update(title=str(title), artist=str(attributes.get('artistName') or '未知歌手'),
                                    expected_duration=float(attributes.get('durationInMillis') or 0) / 1000)
                    status(job, 'downloading', title=safe_filename(title), artist=safe_filename(metadata['artist']))
                yield item

    core.AppleMusicDownloader = SingleSongDownloader
    try:
        reporter = TransferProgress(lambda phase, **fields: status(job, phase, **fields))
        with downloader_progress(reporter):
            errors = await core._download_urls_async(
                urls=[canonical], cookies_path=job / 'cookies.txt', use_wrapper=False,
                output_path=staging, temp_path=job / 'temp', ffmpeg_path=ffmpeg,
                download_mode=DownloadMode.YTDLP, codec_song=SongCodec.AAC_WEB,
                no_synced_lyrics=True, no_exceptions=True, log_level='INFO',
                overwrite=False, audio_format=None, video_format=None)
    finally:
        core.AppleMusicDownloader = original
    if errors or not metadata:
        raise ToolError(network_guard.STATE['category'] or 'download_failed')
    return metadata


def run_job(job, downloader=None, destination=None):
    cookie = job / 'cookies.txt'
    try:
        data = json.loads((job / 'job.json').read_text(encoding='utf-8'))
        song_id, canonical = parse_song_url(data['url'])
        if song_id != data['song_id']:
            raise ToolError('invalid_url')
        validate_cookie(cookie)
        config = json.loads((ROOT / 'tool-config.json').read_text(encoding='utf-8'))
        ffmpeg, ffprobe = config['ffmpeg'], config['ffprobe']
        if not Path(ffmpeg).is_file() or not Path(ffprobe).is_file():
            raise ToolError('dependency')
        os.environ['PATH'] = str(Path(ffmpeg).parent) + os.pathsep + os.environ.get('PATH', '')
        status(job, 'metadata')
        metadata = asyncio.run((downloader or real_download)(job, canonical, song_id, ffmpeg))
        files = list((job / 'staging').rglob('*.m4a'))
        if len(files) != 1:
            raise ToolError('download_failed')
        status(job, 'verifying', title=safe_filename(metadata['title']))
        duration = verify_audio(files[0], ffmpeg, ffprobe, metadata['expected_duration'])
        destination = Path(destination) if destination is not None else Path(data['destination'])
        if not destination.is_absolute() or destination.parent == destination:
            raise ToolError('save_failed')
        final = publish_audio(files[0], destination, metadata['title'], metadata['artist'], song_id, work_id=job.name)
        status(job, 'success', file=str(final), title=safe_filename(metadata['title']),
               duration=duration, bytes=final.stat().st_size, codec='aac')
        return 0
    except ToolError as error:
        status(job, 'error', category=error.category)
        return 1
    except BaseException:
        status(job, 'error', category='local_error')
        return 1
    finally:
        try:
            cookie.unlink(missing_ok=True)
        except OSError:
            status(job, 'error', category='cookie_cleanup')


def main():
    # Contain raw dependency/native messages even when started outside the GUI.
    sink = os.open(os.devnull, os.O_WRONLY)
    os.dup2(sink, 1)
    os.dup2(sink, 2)
    os.close(sink)
    try:
        job = Path(sys.argv[1]).resolve()
        jobs_root = (ROOT / 'private' / 'jobs').resolve()
        if job.parent != jobs_root or len(job.name) != 32 or not all(c in '0123456789abcdef' for c in job.name):
            return 2
        # GUI assigns the Windows kill-on-close job before allowing networking.
        for _ in range(150):
            if (job / 'go.flag').is_file():
                return run_job(job)
            time.sleep(.1)
        return 2
    except BaseException:
        return 2
    finally:
        if 'job' in locals() and job.parent == (ROOT / 'private' / 'jobs').resolve():
            try:
                (job / 'cookies.txt').unlink(missing_ok=True)
            except OSError:
                pass


if __name__ == '__main__':
    sys.exit(main())
