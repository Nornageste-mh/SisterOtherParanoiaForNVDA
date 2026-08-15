# -*- coding: utf-8 -*-
"""逐个 bundle 扫描，崩溃隔离：每个文件在子进程中处理。"""
import os
import subprocess
import sys

BUNDLE_DIR = r"F:\Steam\steamapps\common\Sister Other Paranoia\SisterOtherParanoia_Data\StreamingAssets\aa\StandaloneWindows64"
WORKER = r"D:\Harness工作区\sop-nvda\game-mod\tools\extract_worker.py"

files = sorted(os.listdir(BUNDLE_DIR))
print("total:", len(files), flush=True)
for i, fn in enumerate(files):
    if not fn.endswith(".bundle"):
        continue
    r = subprocess.run([sys.executable, WORKER, os.path.join(BUNDLE_DIR, fn)],
                       capture_output=True, text=True, timeout=300)
    out = r.stdout.strip()
    if r.returncode != 0 and "CRASH" in out:
        print("CRASH:", fn, flush=True)
    elif out:
        print(fn, "->", out, flush=True)
    if (i + 1) % 50 == 0:
        print("...", i + 1, flush=True)
print("done", flush=True)
