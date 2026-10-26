import asyncio
import json
import pathlib
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parent
if ROOT.name == 'tests': ROOT = ROOT.parent
sys.path.insert(0, str(ROOT))
import backend
from tool_core import ToolError, parse_song_url, safe_filename, publish_audio
import network_guard


class ToolTests(unittest.TestCase):
    def test_single_song_normalization_and_album_track(self):
        self.assertEqual(parse_song_url('https://music.apple.com/us/song/title/1234567890')[0], '1234567890')
        self.assertEqual(parse_song_url('https://music.apple.com/cn/album/test/12?i=34&ls=1'), ('34', 'https://music.apple.com/cn/song/34'))
        for bad in ['https://music.apple.com/us/album/123', 'https://music.apple.com/us/playlist/x/pl.123',
                    'https://music.apple.com.evil.invalid/us/song/1', 'http://music.apple.com/us/song/1',
                    'https://x@music.apple.com/us/song/1', 'https://music.apple.com/us/song/1?i=2',
                    'https://music.apple.com/us/album/1?i=2&i=3']:
            with self.assertRaises(ToolError): parse_song_url(bad)

    def test_windows_names_and_unicode_length(self):
        self.assertEqual(safe_filename(' A:/B?* .'), 'A__B__')
        self.assertEqual(safe_filename('CON'), '_CON')
        self.assertEqual(safe_filename('nul.txt'), '_nul.txt')
        self.assertEqual(safe_filename('   .'), '歌曲')
        self.assertEqual(safe_filename('测试曲名（器乐）'), '测试曲名（器乐）')
        self.assertLessEqual(len(safe_filename('😀' * 200).encode('utf-16-le')) // 2, 100)

    def test_atomic_collision_no_overwrite(self):
        with tempfile.TemporaryDirectory(dir=ROOT) as name:
            root = pathlib.Path(name); dest = root / 'out'; dest.mkdir()
            (dest / '歌曲.m4a').write_bytes(b'EXISTING')
            for number in range(4):
                source = root / ('staging' + str(number) + '.m4a'); source.write_bytes(b'NEW')
                result = publish_audio(source, dest, '歌曲', '歌手', '123')
                self.assertTrue(result.is_file()); self.assertFalse(source.exists())
            self.assertEqual((dest / '歌曲.m4a').read_bytes(), b'EXISTING')
            self.assertEqual(len(list(dest.glob('*.m4a'))), 5)

    def test_apple_destination_guard(self):
        network_guard.check_request('https://amp-api.music.apple.com', {'cookie':'SYNTHETIC'})
        network_guard.check_request('https://music.apple.com:443', {'Media-User-Token':'SYNTHETIC'})
        network_guard.check_request('https://aod-ssl.itunes.apple.com/file', {})
        for url, headers in [('https://other.invalid', {}), ('http://music.apple.com', {}),
                             ('https://a.mzstatic.com', {'cookie':'SYNTHETIC'})]:
            with self.assertRaises(RuntimeError): network_guard.check_request(url, headers)

    def test_account_headers_and_nonstandard_ports_fail_closed(self):
        for header in ['Media-User-Token','mEdIa-UsEr-ToKeN','Music-User-Token','X-Apple-DSID',
                       'X-Apple-Session-ID','Proxy-Authorization','X-Custom-Credential','X-Unknown-Header','Sec-Unknown-Private']:
            with self.subTest(header=header):
                with self.assertRaises(RuntimeError):
                    network_guard.check_request('https://a.mzstatic.com', {header:'SYNTHETIC'})
        for url in ['https://music.apple.com:444','https://a.mzstatic.com:444',
                    'http://music.apple.com:443','https://music.apple.com:bad','https://music.apple.com:']:
            with self.subTest(url=url):
                with self.assertRaises(RuntimeError): network_guard.check_request(url,{'Cookie':'SYNTHETIC'})
        network_guard.check_request('https://a.mzstatic.com:443', {'Range':'bytes=0-9','User-Agent':'SYNTHETIC'})

    def prepare_job(self, root):
        previous = backend.ROOT
        backend.ROOT = root
        self.addCleanup(setattr, backend, 'ROOT', previous)
        (root/'tool-config.json').write_text(json.dumps({'ffmpeg':shutil.which('ffmpeg'),'ffprobe':shutil.which('ffprobe')}))
        job = root / 'job'; job.mkdir()
        (job/'job.json').write_text(json.dumps({'url':'https://music.apple.com/us/song/123','song_id':'123'}))
        (job/'cookies.txt').write_text('# Netscape HTTP Cookie File\n.music.apple.com\tTRUE\t/\tTRUE\t0\tmedia-user-token\tSYNTHETIC_TEST_ONLY\n')
        return job

    def test_mock_download_success_integrity_naming_and_cleanup(self):
        with tempfile.TemporaryDirectory(dir=ROOT) as name:
            root=pathlib.Path(name); job=self.prepare_job(root); dest=root/'out'
            async def stub(job, canonical, song_id, ffmpeg):
                self.assertEqual(canonical,'https://music.apple.com/us/song/123')
                (job/'staging').mkdir()
                subprocess.run([ffmpeg,'-hide_banner','-nostdin','-v','error','-f','lavfi','-i','sine=frequency=440:duration=0.4','-c:a','aac',str(job/'staging'/'test.m4a')],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,check=True,creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0),timeout=10)
                return {'title':'合成/测试','artist':'测试','expected_duration':0.4}
            self.assertEqual(backend.run_job(job,stub,dest),0)
            result=json.loads((job/'status.json').read_text())
            self.assertEqual(result['phase'],'success'); self.assertEqual(result['codec'],'aac')
            self.assertEqual(pathlib.Path(result['file']).name,'合成_测试.m4a')
            self.assertFalse((job/'cookies.txt').exists())

    def test_mock_error_sanitized_and_cookie_cleanup(self):
        with tempfile.TemporaryDirectory(dir=ROOT) as name:
            root=pathlib.Path(name); job=self.prepare_job(root)
            async def stub(*args): raise ToolError('authentication')
            self.assertEqual(backend.run_job(job,stub,root/'out'),1)
            result=json.loads((job/'status.json').read_text())
            self.assertEqual(result,{'phase':'error','category':'authentication'})
            self.assertFalse((job/'cookies.txt').exists())

    def test_mock_incomplete_audio_not_published(self):
        with tempfile.TemporaryDirectory(dir=ROOT) as name:
            root=pathlib.Path(name); job=self.prepare_job(root); dest=root/'out'
            async def stub(job,canonical,song_id,ffmpeg):
                (job/'staging').mkdir()
                subprocess.run([ffmpeg,'-hide_banner','-nostdin','-v','error','-f','lavfi','-i','sine=frequency=440:duration=0.4','-c:a','aac',str(job/'staging'/'test.m4a')],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,check=True,creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0),timeout=10)
                return {'title':'短片测试','artist':'测试','expected_duration':116.399}
            self.assertEqual(backend.run_job(job,stub,dest),1)
            self.assertEqual(json.loads((job/'status.json').read_text())['category'],'audio_incomplete')
            self.assertFalse(dest.exists()); self.assertFalse((job/'cookies.txt').exists())


if __name__ == '__main__': unittest.main(verbosity=2)
