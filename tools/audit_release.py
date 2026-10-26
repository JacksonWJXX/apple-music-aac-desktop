"""Audit only explicitly publishable source files. Never open private extras."""
from __future__ import annotations
import hashlib
import json
from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parent.parent
DENIED_SUFFIXES = {'.exe','.dll','.pyd','.so','.whl','.wvd','.apk','.apkm','.m4a','.aac','.mp3','.wav','.flac','.dpapi','.log','.sqlite','.db'}
PRIVATE_PATH = re.compile(r'[A-Za-z]:[\\/]+Users[\\/]+[^\\/\s\'"<>]+', re.I)
LONG_ENCODED = re.compile(r'[A-Za-z0-9+/]{600,}={0,2}')
JWT = re.compile(r'eyJ[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{10,}')


def audit():
    allowed = json.loads((ROOT/'publish-allowlist.json').read_text(encoding='utf-8'))
    if not isinstance(allowed,list) or len(allowed)!=len(set(allowed)):
        raise SystemExit('Invalid allowlist.')
    results=[]; errors=[]
    for relative in sorted(allowed):
        file=ROOT/relative
        if not file.is_file() or file.is_symlink() or not file.resolve().is_relative_to(ROOT.resolve()) or file.suffix.lower() in DENIED_SUFFIXES:
            errors.append('Missing or forbidden allowlist entry: '+relative); continue
        data=file.read_bytes()
        if file.suffix.lower()=='.png':
            if not data.startswith(b'\x89PNG\r\n\x1a\n'): errors.append('Unexpected image type: '+relative)
        else:
            text=data.decode('utf-8-sig')
            if PRIVATE_PATH.search(text) or JWT.search(text) or LONG_ENCODED.search(text): errors.append('Potential personal/secret literal: '+relative)
        results.append({'path':relative,'bytes':len(data),'sha256':hashlib.sha256(data).hexdigest()})
    # Enumerate entry names only; never read unexpected runtime/private contents.
    def check_directory(directory):
        for item in directory.iterdir():
            rel=item.relative_to(ROOT).as_posix()
            if item.is_symlink(): errors.append('Unexpected link entry exists');continue
            if item.is_dir():
                if not any(name.startswith(rel+'/') for name in allowed): errors.append('Unexpected directory exists (contents not inspected)')
                else: check_directory(item)
            elif rel not in allowed: errors.append('Unexpected file exists (contents not inspected)')
    check_directory(ROOT)
    print(json.dumps({'passed':not errors,'files':results,'errors':errors},ensure_ascii=True,indent=2))
    return 0 if not errors else 1


if __name__=='__main__':sys.exit(audit())
