# -*- coding: utf-8 -*-
"""打包 sopAccess 为 .nvda-addon 安装包（zip：manifest.ini + globalPlugins + doc 在根）。"""
import os
import sys
import zipfile

SRC = os.path.join(os.path.dirname(os.path.abspath(__file__)), "sopAccess")
OUT_DIR = r"D:\Harness工作区\sop-nvda\dist"

def _version():
    with open(os.path.join(SRC, "manifest.ini"), "r", encoding="utf-8") as f:
        for line in f:
            if line.strip().startswith("version"):
                return line.split("=", 1)[1].strip()
    return "1.0.0"

OUT = os.path.join(OUT_DIR, "sopAccess-%s.nvda-addon" % _version())

def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    if os.path.exists(OUT):
        os.remove(OUT)
    with zipfile.ZipFile(OUT, "w", zipfile.ZIP_DEFLATED) as z:
        for root, dirs, files in os.walk(SRC):
            dirs[:] = [d for d in dirs if d != "__pycache__"]
            for fn in files:
                if fn.endswith(".pyc"):
                    continue
                full = os.path.join(root, fn)
                rel = os.path.relpath(full, SRC)
                z.write(full, rel)
                print(" + %s" % rel)
    print("打包完成: %s" % OUT)

if __name__ == "__main__":
    sys.exit(main())
