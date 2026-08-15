# -*- coding: utf-8 -*-
"""扫描游戏 Addressables bundles，提取含剧本内容的 TextAsset/MonoBehaviour。"""
import os
import sys
import UnityPy

BUNDLE_DIR = r"F:\Steam\steamapps\common\Sister Other Paranoia\SisterOtherParanoia_Data\StreamingAssets\aa\StandaloneWindows64"
OUT_DIR = r"D:\Harness工作区\sop-nvda\dist\scripts"
os.makedirs(OUT_DIR, exist_ok=True)

KEYWORDS = ["waitClick", "Spider", "Map/", "@goto", "@choice"]
stats = {}
hits = []
total_files = 0

for fn in os.listdir(BUNDLE_DIR):
    if not fn.endswith(".bundle"):
        continue
    total_files += 1
    path = os.path.join(BUNDLE_DIR, fn)
    try:
        env = UnityPy.load(path)
    except Exception as e:
        continue
    for obj in env.objects:
        try:
            tname = obj.type.name
        except Exception:
            continue
        stats[tname] = stats.get(tname, 0) + 1
        if tname == "TextAsset":
            try:
                data = obj.read()
                text = bytes(data.m_Script).decode("utf-8", "replace")
                if "waitClick" in text or "Spider" in text or "Map/" in text:
                    out = os.path.join(OUT_DIR, "TA_" + data.m_Name.replace("/", "_") + ".txt")
                    with open(out, "w", encoding="utf-8") as f:
                        f.write(text)
                    hits.append(("TextAsset", data.m_Name, len(text)))
            except Exception:
                pass
        elif tname == "MonoBehaviour":
            # 尝试读取（可能失败），检查是否有剧本文本
            try:
                data = obj.read()
                blob = data.get_raw_data() or b""
                if b"waitClick" in blob or b"Map/Spider" in blob:
                    hits.append(("MonoBehaviour", getattr(data, "m_Name", "?"), len(blob)))
            except Exception:
                pass

print("扫描文件数:", total_files)
print("类型统计:", dict(sorted(stats.items(), key=lambda x: -x[1])[:15]))
print("命中数:", len(hits))
for h in hits[:30]:
    print(h)
