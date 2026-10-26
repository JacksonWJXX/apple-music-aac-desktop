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
from transfer_progress import TransferProgress, downloader_progress


class ToolTests(unittest.TestCase):
    def test_transfer_rounding_never_reaches_completion(self):
        events=[]
        reporter=TransferProgress(lambda phase,**fields:events.append({'phase':phase,**fields}),interval=0)
        for done in (99949,99950,99999):
            reporter.hook({'status':'downloading','downloaded_bytes':done,'total_bytes':100000})
            self.assertEqual(events[-1]['phase'],'downloading')
            self.assertLessEqual(events[-1]['percent'],99.9)
        reporter.hook({'status':'downloading','downloaded_bytes':100000,'total_bytes':100000})
        self.assertEqual(events[-1]['phase'],'processing')
        self.assertNotIn('percent',events[-1])

    def test_pinned_selector_and_direct_import_receive_hooks_and_restore(self):
        # Instantiate the installed yt-dlp classes, but never call download or
        # any network method. Its real dispatcher chooses HTTP/HLS classes.
        from yt_dlp import YoutubeDL
        import yt_dlp.downloader as selector
        from yt_dlp.downloader import http,hls
        original=(http.HttpFD,hls.HlsFD,selector.HttpFD,selector.HlsFD)
        protocols=dict(selector.PROTOCOL_MAP)
        events=[]
        reporter=TransferProgress(lambda phase,**fields:events.append({'phase':phase,**fields}),interval=0)
        with YoutubeDL({'quiet':True,'noprogress':True,'skip_download':True}) as ydl:
            with downloader_progress(reporter):
                for protocol,url,base in (
                    ('https','https://example.invalid/synthetic.m4a',original[0]),
                    ('m3u8_native','https://example.invalid/synthetic.m3u8',original[1])):
                    chosen=selector.get_suitable_downloader({'url':url,'protocol':protocol},
                        {'hls_prefer_native':True},protocol=protocol)
                    self.assertIsNot(chosen,base)
                    self.assertTrue(issubclass(chosen,base))
                    instance=chosen(ydl,ydl.params)
                    before=len(events)
                    instance._hook_progress({'status':'downloading','downloaded_bytes':25,'total_bytes':100},{})
                    self.assertEqual(len(events),before+1)
                    self.assertEqual(events[-1]['percent'],25)
                # Match the upstream AAC engine's late submodule imports too.
                from yt_dlp.downloader.http import HttpFD
                from yt_dlp.downloader.hls import HlsFD
                self.assertIs(HttpFD,selector.HttpFD)
                self.assertIs(HlsFD,selector.HlsFD)
        self.assertEqual((http.HttpFD,hls.HlsFD,selector.HttpFD,selector.HlsFD),original)
        self.assertEqual(selector.PROTOCOL_MAP,protocols)
        with self.assertRaisesRegex(RuntimeError,'synthetic_stop'):
            with downloader_progress(reporter):
                raise RuntimeError('synthetic_stop')
        self.assertEqual((http.HttpFD,hls.HlsFD,selector.HttpFD,selector.HlsFD),original)
        self.assertEqual(selector.PROTOCOL_MAP,protocols)

    def test_real_counter_progress_unknown_and_verification_boundary(self):
        events=[]
        p=TransferProgress(lambda phase,**fields:events.append({'phase':phase,**fields}),interval=0)
        p.hook({'status':'downloading','downloaded_bytes':25,'total_bytes':100,
                'url':'SYNTHETIC_PRIVATE_URL','info_dict':{'token':'SYNTHETIC_PRIVATE_VALUE'}})
        self.assertEqual(events[-1]['percent'],25)
        self.assertNotIn('SYNTHETIC_PRIVATE',json.dumps(events))
        p.hook({'status':'downloading','downloaded_bytes':55})
        self.assertNotIn('percent',events[-1]);self.assertEqual(events[-1]['bytes_done'],55)
        p.hook({'status':'downloading','downloaded_bytes':100,'total_bytes':100})
        self.assertEqual(events[-1]['phase'],'processing');self.assertNotIn('percent',events[-1])
        p.hook({'status':'finished','downloaded_bytes':100,'total_bytes':100})
        self.assertEqual(events[-1],{'phase':'processing'})

    def test_fragment_progress_and_estimates_are_explicit(self):
        events=[];p=TransferProgress(lambda phase,**fields:events.append({'phase':phase,**fields}),interval=0)
        p.hook({'status':'downloading','fragment_index':2,'fragment_count':8,'downloaded_bytes':40},'hls')
        self.assertEqual(events[-1]['percent'],25);self.assertEqual(events[-1]['fragments_total'],8)
        count=len(events);p.hook({'status':'downloading','downloaded_bytes':90,'total_bytes':100},'http')
        self.assertEqual(len(events),count)
        q=TransferProgress(lambda phase,**fields:events.append({'phase':phase,**fields}),interval=0)
        q.hook({'status':'downloading','downloaded_bytes':10,'total_bytes_estimate':100})
        self.assertTrue(events[-1]['estimated']);self.assertEqual(events[-1]['percent'],10)

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

    def test_long_path_collision_suffixes_have_reserved_space(self):
        # Reproduce the reviewed 57-unit budget, then exercise the minimum 20.
        from tool_core import _units
        for budget in (57,20):
            with tempfile.TemporaryDirectory(dir=ROOT) as name:
                root=pathlib.Path(name);length=240-1-4-budget
                dest=root/('d'*(length-_units(str(root))-1));dest.mkdir()
                self.assertEqual(_units(str(dest)),length)
                outputs=[];contents={}
                for index in range(10):
                    source=root/('source'+str(index));payload=('NEW_'+str(index)).encode();source.write_bytes(payload)
                    # First four items distinguish artists/IDs; the remaining
                    # identical metadata require numbered suffixes to survive.
                    artist='Artist_'+str(index) if index<4 else 'Repeated_Artist'*10
                    song_id=str(12345678901234567890+index) if index<4 else '12345678901234567890'
                    target=publish_audio(source,dest,'T'*120,artist,song_id)
                    self.assertNotIn(target,outputs);self.assertLessEqual(_units(target.stem),budget);self.assertLessEqual(_units(str(target)),240)
                    self.assertEqual(target.suffix,'.m4a');self.assertFalse(source.exists());self.assertEqual(target.read_bytes(),payload)
                    outputs.append(target);contents[target]=payload
                self.assertTrue(any(' (' in p.stem for p in outputs))
                for target,payload in contents.items():self.assertEqual(target.read_bytes(),payload)

    def test_collision_components_are_safe_at_minimum_budget(self):
        from tool_core import _collision_stem,_units
        for title in ['CON.long','COM1.ext','../bad:name', '\U0001f600'*120,' . ']:
            names=set()
            for ordinal in (None,2,3,998):
                stem=_collision_stem(title,r'../AUX:unsafe\\artist'*10,20,ordinal)
                self.assertLessEqual(_units(stem),20);self.assertTrue(stem)
                self.assertFalse(any(c in stem for c in '<>:"/\\|?*'))
                self.assertEqual(stem,stem.rstrip(' .'));self.assertNotIn(stem,names);names.add(stem)

    def test_insufficient_path_budget_preserves_source(self):
        from tool_core import _units
        with tempfile.TemporaryDirectory(dir=ROOT) as name:
            root=pathlib.Path(name);length=240-1-4-19
            dest=root/('d'*(length-_units(str(root))-1));source=root/'source';source.write_bytes(b'UNCHANGED')
            with self.assertRaisesRegex(ToolError,'path_too_long'):publish_audio(source,dest,'title','artist','1')
            self.assertEqual(source.read_bytes(),b'UNCHANGED');self.assertFalse(list(dest.glob('*.m4a')))

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
                return {'album':'Synthetic Download Album','title':'合成/测试','artist':'测试','expected_duration':0.4}
            self.assertEqual(backend.run_job(job,stub,dest),0)
            result=json.loads((job/'status.json').read_text())
            self.assertEqual(result['phase'],'success'); self.assertEqual(result['codec'],'aac')
            self.assertEqual(pathlib.Path(result['file']).name,'合成_测试.m4a')
            self.assertEqual(pathlib.Path(result['file']).parent.name,'Synthetic Download Album')
            self.assertEqual(result['album'],'Synthetic Download Album')
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
            self.assertFalse(list(dest.rglob('*.m4a'))); self.assertFalse((job/'cookies.txt').exists())


    def test_library_lock_prevents_download_callback(self):
        from album_library import library_lock
        with tempfile.TemporaryDirectory(dir=ROOT) as name:
            root=pathlib.Path(name);job=self.prepare_job(root);dest=root/'out';dest.mkdir();called=[]
            async def stub(*args):called.append(True);raise AssertionError('must not authenticate/download')
            with library_lock(dest):self.assertEqual(backend.run_job(job,stub,dest),1)
            self.assertEqual(called,[]);self.assertEqual(json.loads((job/'status.json').read_text())['category'],'library_busy');self.assertFalse((job/'cookies.txt').exists())

if __name__ == '__main__': unittest.main(verbosity=2)
