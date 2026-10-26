import errno
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT=Path(__file__).resolve().parent.parent
sys.path.insert(0,str(ROOT))
import album_library as lib
from tool_core import ToolError,safe_filename


class AlbumLibraryTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory(dir=ROOT.parent);self.addCleanup(self.temp.cleanup)
        self.base=Path(self.temp.name);self.root=self.base/'music';self.root.mkdir()
        self.ffmpeg=shutil.which('ffmpeg');self.ffprobe=shutil.which('ffprobe')
        self.assertTrue(self.ffmpeg and self.ffprobe)

    def audio(self,path,album='Synthetic Album'):
        path.parent.mkdir(parents=True,exist_ok=True)
        args=[self.ffmpeg,'-hide_banner','-nostdin','-v','error','-f','lavfi','-i','sine=frequency=440:duration=0.2']
        if album is not None:args+=['-metadata','album='+album]
        args+=['-y',str(path)]
        subprocess.run(args,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,check=True,timeout=15,creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
        return path

    def plan(self):return lib.make_plan(self.root,self.ffprobe)
    def apply(self,plan,**kw):
        with lib.library_lock(self.root):return lib.execute_plan(self.root,plan,**kw)

    def test_grouping_unknown_embedded_tags_and_idempotence(self):
        self.audio(self.root/'one.m4a');self.audio(self.root/'two.m4a');self.audio(self.root/'none.m4a',None)
        before={p.name:p.read_bytes() for p in self.root.glob('*.m4a')}
        plan=self.plan();self.assertEqual(plan['moves'],3);self.assertFalse((self.root/'Synthetic Album').exists())
        result=self.apply(plan);self.assertEqual(result['moved'],3)
        for name in ['one.m4a','two.m4a']:self.assertEqual((self.root/'Synthetic Album'/name).read_bytes(),before[name])
        self.assertEqual((self.root/'未知专辑'/'none.m4a').read_bytes(),before['none.m4a'])
        repeat=self.plan();self.assertEqual(repeat['moves'],0);self.assertEqual(repeat['already'],3)
        self.assertFalse((self.root/'Synthetic Album'/'Synthetic Album').exists())
        events=[json.loads(line) for line in (self.root/result['journal']).read_text('utf-8').splitlines()]
        self.assertEqual(sum(e['event']=='intent' for e in events),3);self.assertEqual(sum(e['event']=='moved' for e in events),3)
        for event in events:
            for key in ('source','target'):
                if key in event:self.assertFalse(Path(event[key]).is_absolute())

    def test_formats_and_non_music_files(self):
        for suffix in ['.mp3','.flac','.wav','.ogg','.m4a']:
            file=self.audio(self.root/('song'+suffix),'Embedded '+suffix)
            self.assertEqual(lib.audio_album(file,self.ffprobe),'Embedded '+suffix)
        (self.root/'notes.txt').write_text('not music')
        (self.root/'fake.mp3').write_text('not an MPEG stream')
        plan=self.plan();self.assertEqual(plan['moves'],5);self.assertEqual(plan['skipped'],1)
        self.assertFalse(any(e['source']=='notes.txt' for e in plan['entries']))

    def test_safe_names_reserved_long_and_distinct_sanitized_albums(self):
        for name in ['../outside',r'..\..\outside','CON','nul.txt','A:B','A/B','x'*500,'😀'*500,'COM\u00b9']:
            folder=lib.album_component(name,self.root)
            self.assertNotIn('/',folder);self.assertNotIn('\\',folder);self.assertLessEqual(lib.units(folder),80)
            self.assertEqual(folder,lib.album_component(name,self.root))
        self.assertNotEqual(lib.album_component('A:B',self.root),lib.album_component('A/B',self.root))
        self.assertTrue(safe_filename('COM\u00b9').startswith('_'))
        self.audio(self.root/'song.m4a','../outside');result=self.apply(self.plan());self.assertEqual(result['moved'],1)
        self.assertFalse((self.base/'outside').exists())

    def test_collision_never_overwrites(self):
        original=self.audio(self.root/'Synthetic Album'/'same.m4a');content=original.read_bytes()
        self.audio(self.root/'first'/'same.m4a');self.audio(self.root/'second'/'same.m4a')
        plan=self.plan();targets=[e['target'] for e in plan['entries'] if e['action']=='move'];self.assertEqual(len(set(targets)),2)
        result=self.apply(plan);self.assertEqual(result['moved'],2);self.assertEqual(original.read_bytes(),content)
        self.assertEqual(len(list((self.root/'Synthetic Album').glob('*.m4a'))),3)

    def test_config_named_album_directories_are_not_application_markers(self):
        for index,album in enumerate(['tool-config.json','TOOL-CONFIG.JSON']):
            self.audio(self.root/('track'+str(index)+'.m4a'),album)
            result=self.apply(self.plan());self.assertEqual(result['moved'],1)
            self.assertTrue((self.root/album/('track'+str(index)+'.m4a')).is_file())
            self.assertEqual(lib.library_root(self.root),self.root)
            repeat=self.plan();self.assertEqual(repeat['moves'],0);self.assertEqual(repeat['already'],index+1)
        for index,name in enumerate(['tool-config.json','TOOL-CONFIG.JSON']):
            application=self.base/('app-marker'+str(index));application.mkdir()
            (application/name).write_text('SYNTHETIC_CONFIG_ONLY')
            if os.name=='nt' or name=='tool-config.json':
                with self.assertRaisesRegex(ToolError,'not_music_folder'):lib.library_root(application)

    def test_config_named_directory_does_not_skip_parent_music(self):
        group=self.root/'collection';group.mkdir();(group/'tool-config.json').mkdir()
        self.audio(group/'song.m4a')
        plan=self.plan();self.assertEqual(plan['moves'],1)
        self.assertEqual(self.apply(plan)['moved'],1);self.assertEqual(self.plan()['moves'],0)

    def test_cancel_before_and_between_moves(self):
        self.audio(self.root/'a.m4a');self.audio(self.root/'b.m4a');plan=self.plan()
        result=self.apply(plan,cancel=lambda:True);self.assertTrue(result['cancelled']);self.assertEqual(result['moved'],0);self.assertFalse((self.root/lib.RECORDS).exists())
        progress={'done':0}
        result=self.apply(plan,cancel=lambda:progress['done']>=1,progress=lambda **f:progress.update(f))
        self.assertTrue(result['cancelled']);self.assertEqual(result['moved'],1);self.assertEqual(len(list(self.root.glob('*.m4a'))),1)

    def test_changed_source_and_new_collision_are_preserved(self):
        source=self.audio(self.root/'a.m4a');plan=self.plan();source.write_bytes(source.read_bytes()+b'CHANGED')
        result=self.apply(plan);self.assertEqual(result['failed'],1);self.assertTrue(source.exists())
        plan=self.plan();row=next(e for e in plan['entries'] if e['action']=='move');target=self.root/row['target'];target.parent.mkdir();target.write_bytes(b'EXISTING')
        result=self.apply(plan);self.assertEqual(result['failed'],1);self.assertEqual(target.read_bytes(),b'EXISTING');self.assertTrue(source.exists())

    def test_traversal_forged_plan_rejected_before_move(self):
        source=self.audio(self.root/'a.m4a');plan=self.plan();plan['entries'][0]['target']='../escape.m4a'
        with self.assertRaises(ToolError):self.apply(plan)
        self.assertTrue(source.exists());self.assertFalse((self.base/'escape.m4a').exists())

    def test_cross_volume_failure_no_copy_delete_fallback(self):
        source=self.audio(self.root/'a.m4a');content=source.read_bytes();plan=self.plan()
        with patch.object(lib,'rename_no_replace',side_effect=OSError(errno.EXDEV,'synthetic')):
            result=self.apply(plan)
        self.assertEqual(result['failed'],1);self.assertEqual(source.read_bytes(),content)
        self.assertFalse(list((self.root/'Synthetic Album').glob('*.m4a')))

    @unittest.skipUnless(os.name=='nt','Windows sharing semantics')
    def test_actual_windows_file_in_use_preserves_source(self):
        import ctypes
        source=self.audio(self.root/'a.m4a');plan=self.plan();content=source.read_bytes()
        kernel=ctypes.WinDLL('kernel32',use_last_error=True);kernel.CreateFileW.argtypes=[ctypes.c_wchar_p,ctypes.c_uint,ctypes.c_uint,ctypes.c_void_p,ctypes.c_uint,ctypes.c_uint,ctypes.c_void_p];kernel.CreateFileW.restype=ctypes.c_void_p;kernel.CloseHandle.argtypes=[ctypes.c_void_p]
        handle=kernel.CreateFileW(str(source),0x80000000,1,None,3,0,None);self.assertNotEqual(handle,ctypes.c_void_p(-1).value)
        try:result=self.apply(plan)
        finally:kernel.CloseHandle(handle)
        self.assertEqual(result['failed'],1);self.assertEqual(source.read_bytes(),content)

    def test_root_lock_excludes_second_operation(self):
        with lib.library_lock(self.root):
            with self.assertRaisesRegex(ToolError,'library_busy'):
                with lib.library_lock(self.root):pass
        with lib.library_lock(self.root):pass

    def test_journal_write_failure_before_move(self):
        source=self.audio(self.root/'a.m4a');plan=self.plan()
        with patch.object(lib.os,'fsync',side_effect=OSError('synthetic disk failure')):
            with self.assertRaises(OSError):self.apply(plan)
        self.assertTrue(source.exists());self.assertFalse((self.root/'Synthetic Album').exists())

    def test_reparse_attribute_guard_without_creating_links(self):
        from types import SimpleNamespace
        source=self.audio(self.root/'a.m4a');plan=self.plan();blocked=self.root/'Synthetic Album';blocked.mkdir()
        original=Path.lstat
        def flagged(path):
            info=original(path)
            if path==blocked:return SimpleNamespace(st_mode=info.st_mode,st_file_attributes=0x400)
            return info
        with patch.object(Path,'lstat',flagged):
            with self.assertRaises(ToolError):lib.checked_path(blocked)
            with self.assertRaises(ToolError):self.apply(plan)
        self.assertTrue(source.exists())

    def test_after_move_journal_failure_preserves_audio_and_intent(self):
        self.audio(self.root/'a.m4a');self.audio(self.root/'b.m4a');plan=self.plan();calls={'n':0}
        def failure(fd):
            calls['n']+=1
            if calls['n']==3:raise OSError('synthetic sync failure after rename')
        with patch.object(lib.os,'fsync',side_effect=failure):result=self.apply(plan)
        self.assertEqual(result['moved'],1);self.assertTrue(result['fatal']);self.assertEqual(len(list(self.root.rglob('*.m4a'))),2)
        events=[json.loads(line) for line in (self.root/result['journal']).read_text('utf-8').splitlines()]
        self.assertTrue(any(e['event']=='intent' for e in events))

    def test_long_source_name_stays_within_path_budget(self):
        source=self.audio(self.root/(('long-name-'*15)+'.m4a'),'Long album '+('x'*300));plan=self.plan()
        self.assertEqual(plan['moves'],1);target=self.root/plan['entries'][0]['target'];self.assertLessEqual(lib.units(target),240)
        result=self.apply(plan);self.assertEqual(result['moved'],1);self.assertTrue(target.is_file());self.assertFalse(source.exists());self.assertEqual(self.plan()['moves'],0)

    @unittest.skipUnless(os.name=='nt','Windows junction test')
    def test_reparse_scan_and_target_rejected(self):
        target=self.base/'outside';target.mkdir();self.audio(target/'outside.m4a')
        link=self.root/'link'
        result=subprocess.run(['cmd','/c','mklink','/J',str(link),str(target)],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
        if result.returncode:self.skipTest('Junction creation unavailable; no security change attempted')
        try:
            with patch.object(lib,'audio_album',wraps=lib.audio_album) as reader:
                plan=self.plan();self.assertEqual(plan['moves'],0);self.assertEqual(reader.call_count,0)
            with self.assertRaises(ToolError):lib.library_root(link)
        finally:os.rmdir(link)
        source=self.audio(self.root/'a.m4a');plan=self.plan();link=self.root/'Synthetic Album'
        result=subprocess.run(['cmd','/c','mklink','/J',str(link),str(target)],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
        self.assertEqual(result.returncode,0)
        try:
            with self.assertRaises(ToolError):self.apply(plan)
            self.assertTrue(source.exists());self.assertFalse((target/'a.m4a').exists())
        finally:os.rmdir(link)


if __name__=='__main__':unittest.main(verbosity=2)
