"""GUI offline organizer protocol. No credential or Apple/network imports."""
from pathlib import Path
import json
import os
import re
import sys
import time
ROOT=Path(__file__).resolve().parent
sys.path.insert(0,str(ROOT))
from album_library import library_root, library_lock, make_plan, execute_plan, checked_path
from tool_core import ToolError


def atomic(path,value):
    temp=path.with_name(path.name+'.new')
    temp.write_text(json.dumps(value,ensure_ascii=True),encoding='utf-8')
    temp.replace(path)


def run(job):
    cancel=lambda:(job/'cancel.flag').exists()
    def status(phase,**fields):atomic(job/'status.json',{'phase':phase,**fields})
    try:
        data=json.loads((job/'job.json').read_text(encoding='utf-8'))
        root=library_root(data['root'])
        config=json.loads((ROOT/'tool-config.json').read_text(encoding='utf-8'))
        ffprobe=Path(config['ffprobe'])
        if not ffprobe.is_file():raise ToolError('dependency')
        with library_lock(root):
            status('planning',done=0,total=0)
            plan=make_plan(root,ffprobe,cancel,lambda **f:status('planning',**f),spool_directory=job)
            atomic(job/'plan.json',plan)
            if not plan['moves']:
                status('done',moved=0,failed=0,already=plan['already'],skipped=plan['skipped'],journal='');return 0
            status('ready',moves=plan['moves'],already=plan['already'],skipped=plan['skipped'])
            deadline=time.monotonic()+900
            while not (job/'approve.flag').exists():
                if cancel() or time.monotonic()>deadline:raise ToolError('cancelled')
                time.sleep(.1)
            if cancel():raise ToolError('cancelled')
            status('organizing',done=0,total=plan['moves'],moved=0,failed=0)
            result=execute_plan(root,plan,cancel,lambda **f:status('organizing',**f))
            status('cancelled' if result['cancelled'] else 'error' if result['fatal'] else 'done',**result)
            return 1 if result['fatal'] else 0
    except ToolError as error:
        status('cancelled' if error.category=='cancelled' else 'error',category=error.category)
        return 0 if error.category=='cancelled' else 1
    except BaseException:
        status('error',category='organize_local_error');return 1


def main():
    sink=os.open(os.devnull,os.O_WRONLY);os.dup2(sink,1);os.dup2(sink,2);os.close(sink)
    try:
        job=Path(sys.argv[1])
        expected=ROOT/'private'/'organize-jobs'
        checked_path(job)
        if job.parent!=expected or not re.fullmatch('[0-9a-f]{32}',job.name):return 2
        for _ in range(150):
            if (job/'go.flag').exists():return run(job)
            time.sleep(.1)
        return 2
    except BaseException:return 2


if __name__=='__main__':sys.exit(main())
