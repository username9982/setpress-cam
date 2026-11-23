#!/usr/bin/env python3
"""セットプレス CAM の Windows 版を作る（開発用）: SetPressCam.exe（dotnet publish、.NET 10）→ インストーラー（Inno Setup）。

  python windows/build_win.py                 exe とインストーラー
  python windows/build_win.py --no-installer  exe（ポータブル版のフォルダ）だけ

出力:
  windows/build/dist/SetPressCam/                      ポータブル版（.NET 10 が入っている PC なら、どこに置いても動く）
  windows/build/output/SetPressCam-<版>-setup.exe      インストーラー（GitHub Releases に付ける）

要るもの（作る PC だけ）: .NET 10 SDK（dotnet）、Inno Setup 6、windows/third_party/ の .NET 10 デスクトップ ランタイムの
インストーラー（RUNTIME。git には入れない。RUNTIME_URL から取る。SHA-512 を確かめる）。

アプリは framework-dependent の SetPressCam.exe 1 つ。インストーラーは Microsoft のランタイムのインストーラーを同梱し、
.NET 10 が無い PC でだけ実行する。版は Web 版（app.js の APP_VERSION）と同じにする。中身を変えたら必ず版を上げる。
"""

import argparse
import hashlib
import os
import re
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
PROJ = os.path.join(HERE, "SetPressCam")
EXE = "SetPressCam.exe"
BUILD = os.path.join(HERE, "build")
DIST = os.path.join(BUILD, "dist", "SetPressCam")
OUTPUT = os.path.join(BUILD, "output")

VERSION = "0.17.10"  # Program.VERSION、SetPressCam.csproj の <Version>、app.js の APP_VERSION と同じにする

# .NET 10 デスクトップ ランタイム（csproj の net10.0-windows に合わせる）。ハッシュは Microsoft の
# https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json に載っている値
RUNTIME_VERSION = "10.0.14"
RUNTIME = os.path.join(HERE, "third_party", f"windowsdesktop-runtime-{RUNTIME_VERSION}-win-x64.exe")
RUNTIME_URL = ("https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/"
               f"{RUNTIME_VERSION}/windowsdesktop-runtime-{RUNTIME_VERSION}-win-x64.exe")
RUNTIME_SHA512 = ("337dacab1636219147b3fe75e451bf5b1c17bca2c86328266d40c4ad1ff0a1e8"
                  "391c4746ca7a5b702b65b61e8bfe834566cfc1b548cc10aef90b441357b10b69")

ISCC_CANDIDATES = [
    os.path.expandvars(r"%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"),
    r"C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    r"C:\Program Files\Inno Setup 6\ISCC.exe",
]


def step(msg):
    print(f"== {msg}", flush=True)


def run(cmd, **kw):
    r = subprocess.run(cmd, **kw)
    if r.returncode != 0:
        sys.exit(f"failed ({r.returncode}): {' '.join(cmd)}")


def read(path):
    with open(path, encoding="utf-8-sig") as f:
        return f.read()


def check_versions():
    """版の番号が 4 か所でそろっているか"""
    found = {
        "Program.cs": re.search(r'VERSION = "([^"]+)"', read(os.path.join(PROJ, "Program.cs"))).group(1),
        "SetPressCam.csproj": re.search(r"<Version>([^<]+)</Version>", read(os.path.join(PROJ, "SetPressCam.csproj"))).group(1),
        "app.js": re.search(r"APP_VERSION = '([^']+)'", read(os.path.join(ROOT, "app.js"))).group(1),
    }
    bad = {k: v for k, v in found.items() if v != VERSION}
    if bad:
        sys.exit(f"version mismatch: build_win.py {VERSION}, " + ", ".join(f"{k} {v}" for k, v in bad.items()))


def check_runtime():
    """同梱するランタイムのインストーラーが Microsoft のものか（SHA-512）"""
    if not os.path.exists(RUNTIME):
        sys.exit(f"{RUNTIME} is missing.\nDownload it from {RUNTIME_URL}\ninto {os.path.dirname(RUNTIME)}")
    h = hashlib.sha512()
    with open(RUNTIME, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    if h.hexdigest() != RUNTIME_SHA512:
        sys.exit(f"{RUNTIME} is not Microsoft's .NET {RUNTIME_VERSION} Desktop Runtime installer (SHA-512 differs)")


def build_exe():
    step("dotnet publish")
    out = os.path.join(BUILD, "release")
    if os.path.isdir(out):
        shutil.rmtree(out)
    # framework-dependent の exe 1 つ（ランタイム、RID、単一ファイルは csproj に書いてある）
    run(["dotnet", "publish", os.path.join(PROJ, "SetPressCam.csproj"), "-c", "Release", "-o", out],
        stdout=subprocess.DEVNULL)
    if os.path.isdir(DIST):
        shutil.rmtree(DIST)
    os.makedirs(DIST)
    shutil.copy(os.path.join(out, EXE), DIST)
    print(f"   {os.path.join(DIST, EXE)} ({os.path.getsize(os.path.join(DIST, EXE)) // 1024} KB)")


DOTNET_MIT = """The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors

All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
"""


def make_licenses():
    """同梱したもののライセンス表記。exe の横に置く（これがあると、アプリは配った形だと分かる）。"""
    step("licenses")
    parts = [
        ("SetPressCam (セットプレス CAM)",
         "Runs on .NET 10 (Windows Desktop Runtime). No other third-party code is included.\n"
         "The installer carries Microsoft's unmodified installer of the .NET Desktop Runtime\n"
         f"(windowsdesktop-runtime-{RUNTIME_VERSION}-win-x64.exe) and runs it only when .NET 10 is not installed.\n"
         "The runtime installs into Program Files\\dotnet with its own license and third-party notices."),
        (f".NET {RUNTIME_VERSION} Desktop Runtime (MIT License) - https://dotnet.microsoft.com/", DOTNET_MIT),
    ]
    path = os.path.join(DIST, "THIRD_PARTY_LICENSES.txt")
    with open(path, "w", encoding="utf-8", newline="\r\n") as f:
        for title, body in parts:
            f.write("=" * 78 + "\n" + title + "\n" + "=" * 78 + "\n\n" + body.strip() + "\n\n")


def build_installer():
    step("Inno Setup")
    iscc = next((p for p in ISCC_CANDIDATES if os.path.exists(p)), None)
    if not iscc:
        sys.exit("Inno Setup 6 (ISCC.exe) not found")
    # BOM 付き UTF-8 で渡す（無いと日本語が化ける）
    iss = os.path.join(BUILD, "setpresscam.iss")
    s = read(os.path.join(HERE, "setpresscam.iss"))
    with open(iss, "w", encoding="utf-8-sig", newline="\r\n") as f:
        f.write(s)
    run([iscc, "/Q", f"/DAppVersion={VERSION}", f"/DSourceDir={DIST}", f"/DOutputDir={OUTPUT}",
         f"/DIconFile={os.path.join(PROJ, 'setpresscam.ico')}", f"/DDotnetRuntime={RUNTIME}", iss])
    out = os.path.join(OUTPUT, f"SetPressCam-{VERSION}-setup.exe")
    print(f"   {out} ({os.path.getsize(out) // (1024 * 1024)} MB)")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--no-installer", action="store_true")
    args = ap.parse_args()
    check_versions()
    os.makedirs(BUILD, exist_ok=True)
    if not args.no_installer:
        check_runtime()
    build_exe()
    make_licenses()
    if not args.no_installer:
        build_installer()


if __name__ == "__main__":
    main()
