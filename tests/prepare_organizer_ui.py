"""Create only synthetic fixtures in a NEW directory for OrganizerUiTests."""
from pathlib import Path
import hashlib
import json
import shutil
import subprocess
import sys

source=Path(__file__).resolve().parent.parent
base=Path(sys.argv[1]).absolute()
if base.exists() and any(base.iterdir()):raise SystemExit('Use a new empty fixture directory.')
base.mkdir(parents=True,exist_ok=True)
ffmpeg,ffprobe=shutil.which('ffmpeg'),shutil.which('ffprobe')
if not ffmpeg or not ffprobe:raise SystemExit('Existing FFmpeg/ffprobe required.')
for mode in ['cancel','close-preview','confirm','close-main','download-busy']:
    app=base/mode;app.mkdir();music=app/'music';music.mkdir()
    for name in ['organizer.py','album_library.py','tool_core.py']:shutil.copyfile(source/name,app/name)
    shutil.copyfile(source/'tests/queue_ui_mock.py',app/'backend.py')
    (app/'tool-config.json').write_text(json.dumps({'python':sys.executable,'ffprobe':ffprobe,'ffmpeg':ffmpeg}),encoding='utf-8')
    (app/'settings.json').write_text(json.dumps({'OutputFolder':str(music),'ScanFolder':''}),encoding='utf-8')
    hashes={}
    for name in ['one','two']:
        path=music/(name+'.m4a')
        subprocess.run([ffmpeg,'-hide_banner','-nostdin','-v','error','-f','lavfi','-i','sine=frequency=440:duration=0.2','-metadata','album=Synthetic UI Album',str(path)],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,check=True,timeout=15,creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
        hashes[path.name]=hashlib.sha256(path.read_bytes()).hexdigest()
    (app/'fixture-hashes.json').write_text(json.dumps(hashes),encoding='utf-8')
(base/'synthetic-fixture.txt').write_text('SYNTHETIC_ORGANIZER_UI_ONLY',encoding='utf-8')
print('Synthetic fixtures ready; no real library inspected.')
