# -*- coding: utf-8 -*-
"""单文件扫描 worker：输出命中的 TextAsset 文本到输出目录。"""
import os
import sys
import UnityPy

BUNDLE = sys.argv[1]
OUT_DIR = r"D:\Harness工作区\sop-nvda\dist\scripts"
os.makedirs(OUT_DIR, exist_ok=True)

def main():
    env = UnityPy.load(BUNDLE)
    hits = []
    for obj in env.objects:
        try:
            tname = obj.type.name
        except Exception:
            continue
        if tname == "TextAsset":
            try:
                data = obj.read()
                text = bytes(data.m_Script).decode("utf-8", "replace")
                name = data.m_Name
                if any(k in text for k in ("waitClick", "Spider", "Map/", "@goto", "@choice")):
                    out = os.path.join(OUT_DIR, "TA_" + name.replace("/", "_").replace(" ", "_") + ".txt")
                    with open(out, "w", encoding="utf-8") as f:
                        f.write(text)
                    hits.append("TA:" + name + ":" + str(len(text)))
            except Exception:
                pass
    if hits:
        print(";".join(hits), flush=True)

if __name__ == "__main__":
    main()
