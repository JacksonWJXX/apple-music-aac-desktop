"""Offline GUI orchestration fixture. No Apple imports, network or real audio."""
from pathlib import Path
import json, os, sys, time
job=Path(sys.argv[1]); root=job.parent.parent.parent
for _ in range(200):
    if (job/'go.flag').exists(): break
    time.sleep(.01)
data=json.loads((job/'job.json').read_text())
marker=root/'active-worker'
try:
    fd=os.open(marker,os.O_CREAT|os.O_EXCL|os.O_WRONLY);os.close(fd)
except FileExistsError:
    (root/'concurrent-failure').write_text('overlap');sys.exit(1)
def state(**fields):
    f=job/'status.new';f.write_text(json.dumps(fields));f.replace(job/'status.json')
try:
    title='Synthetic song '+data['song_id']
    state(phase='metadata',title=title);time.sleep(.35)
    if data['song_id']=='9':time.sleep(30)
    state(phase='downloading',title=title,percent=25.0,bytes_done=25,bytes_total=100);time.sleep(.4)
    if data['song_id']=='2':state(phase='error',category='network');sys.exit(1)
    state(phase='verifying',title=title);time.sleep(.3)
    state(phase='success',title=title,file=str(root/'audio'/(title+'.m4a')))
finally:
    (job/'cookies.txt').unlink(missing_ok=True);marker.unlink(missing_ok=True)
