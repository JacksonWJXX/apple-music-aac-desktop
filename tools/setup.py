"""Local Windows build/setup; never handles real login data."""
from __future__ import annotations
import argparse
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import sys
import winreg

ROOT = Path(__file__).resolve().parent.parent


def run(args):
    subprocess.run([str(value) for value in args], cwd=ROOT, check=True)


def main():
    parser = argparse.ArgumentParser(description='Build the source-only Windows AAC desktop frontend.')
    parser.add_argument('--acknowledge-third-party-risk', action='store_true')
    parser.add_argument('--compile-only', action='store_true')
    parser.add_argument('--shortcut', action='store_true')
    options = parser.parse_args()
    if os.name != 'nt' or sys.version_info[:2] != (3, 13) or platform.machine().lower() not in ('amd64', 'x86_64'):
        raise SystemExit('Supported setup: Windows x64 and Python 3.13 only.')
    if not options.compile_only and not options.acknowledge_third_party_risk:
        raise SystemExit('Read README, SECURITY and THIRD_PARTY_NOTICES first. Dependency setup (including the reviewed source-only construct packaging script) requires --acknowledge-third-party-risk. No installation performed.')
    compiler = Path(os.environ.get('WINDIR', r'C:\Windows')) / 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    if not compiler.is_file():
        raise SystemExit('Windows .NET Framework C# compiler not found. No security settings will be changed.')
    with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full') as key:
        release = winreg.QueryValueEx(key, 'Release')[0]
    if release < 528040:
        raise SystemExit('.NET Framework 4.8 or newer is required for this DPI-aware build. No system update was attempted.')
    ffmpeg, ffprobe = shutil.which('ffmpeg'), shutil.which('ffprobe')
    if not ffmpeg or not ffprobe:
        raise SystemExit('Install FFmpeg/ffprobe yourself from a trusted source and put both on PATH first.')
    python = ROOT / '.runtime/Scripts/python.exe'
    if not options.compile_only:
        if not python.is_file():
            run([sys.executable, '-m', 'venv', ROOT / '.runtime'])
        # Build tooling is distinct from the hash-locked runtime graph.
        run([python, '-I', '-m', 'pip', '--isolated', 'install', '--index-url', 'https://pypi.org/simple',
             '--only-binary=:all:', 'setuptools==84.2.1', 'wheel==0.48.0', 'packaging==26.3'])
        run([python, '-I', '-m', 'pip', '--isolated', 'install', '--index-url', 'https://pypi.org/simple',
             '--require-hashes', '--only-binary=:all:', '--no-binary=construct', '--no-build-isolation',
             '-r', ROOT / 'requirements.lock'])
        run([python, '-I', '-m', 'pip', 'check'])
    run([compiler, '/nologo', '/target:winexe', '/codepage:65001', '/out:AppleMusicAAC.exe', '/win32manifest:app.manifest',
         '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll',
         '/reference:System.Security.dll', '/reference:System.Web.dll',
         '/reference:System.Web.Extensions.dll', 'AppleMusicTool.cs'])
    shutil.copyfile(ROOT / 'App.config', ROOT / 'AppleMusicAAC.exe.config')
    (ROOT / 'tool-config.json').write_text(json.dumps({'python': '.runtime/Scripts/python.exe',
                                                      'ffmpeg': ffmpeg, 'ffprobe': ffprobe}, indent=2), encoding='utf-8')
    if options.shortcut:
        # Only generated application paths enter this fixed command, never login data.
        quote = lambda text: "'" + str(text).replace("'", "''") + "'"
        command = "$d=[Environment]::GetFolderPath('Desktop');$p=Join-Path $d 'Apple Music AAC Desktop.lnk';if(Test-Path -LiteralPath $p){throw 'Shortcut already exists; preserve it and create one manually.'};$s=New-Object -ComObject WScript.Shell;$l=$s.CreateShortcut($p);$l.TargetPath=" + quote(ROOT / 'AppleMusicAAC.exe') + ";$l.WorkingDirectory=" + quote(ROOT) + ";$l.Save()"
        run(['powershell.exe', '-NoProfile', '-Command', command])
    print('Build complete. Open AppleMusicAAC.exe and use the manual login guide. No credentials were imported.')


if __name__ == '__main__':
    main()
