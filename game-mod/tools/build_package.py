# -*- coding: utf-8 -*-
"""组装面向新用户的完整安装包（BepInEx 运行时 + 游戏插件 + NVDA 插件 + 安装脚本）。"""
import os
import shutil
import zipfile

ROOT = r"D:\Harness工作区\sop-nvda"
GAME = r"F:\Steam\steamapps\common\Sister Other Paranoia"
DIST = os.path.join(ROOT, "dist")
PKG = os.path.join(DIST, "安装包")
OUT = os.path.join(DIST, "sopAccess-安装包-v1.3.0.zip")

# 1. 准备安装包目录结构
for sub in ("BepInEx", "BepInEx/core", "BepInEx/plugins"):
    os.makedirs(os.path.join(PKG, sub), exist_ok=True)

# 2. 复制 BepInEx 运行时（从已部署的游戏目录复制，保证与游戏兼容；排除运行时残留）
BEPINEX_SRC = os.path.join(GAME, "BepInEx")
for name in os.listdir(BEPINEX_SRC):
    if name in ("cache", "config", "LogOutput.log"):
        continue  # 运行时生成物，新用户环境会自行重建
    s = os.path.join(BEPINEX_SRC, name)
    d = os.path.join(PKG, "BepInEx", name)
    if os.path.isdir(s):
        shutil.copytree(s, d, dirs_exist_ok=True)
    else:
        shutil.copy2(s, d)
for f in ("winhttp.dll", "doorstop_config.ini", ".doorstop_version"):
    s = os.path.join(GAME, f)
    if os.path.exists(s):
        shutil.copy2(s, os.path.join(PKG, f))

# 3. 复制游戏辅助插件（最新构建）
shutil.copy2(os.path.join(ROOT, "game-mod", "SopAccess", "bin", "Release", "SopAccess.dll"),
             os.path.join(PKG, "SopAccess.dll"))

# 4. NVDA 插件安装包（最新）
nvda_addon = os.path.join(DIST, "sopAccess-1.3.0.nvda-addon")
if os.path.exists(nvda_addon):
    shutil.copy2(nvda_addon, os.path.join(PKG, os.path.basename(nvda_addon)))

# 5. 打包 zip
if os.path.exists(OUT):
    os.remove(OUT)
with zipfile.ZipFile(OUT, "w", zipfile.ZIP_DEFLATED) as z:
    for root, dirs, files in os.walk(PKG):
        # 排除 __pycache__ 等
        dirs[:] = [d for d in dirs if d != "__pycache__"]
        for fn in files:
            full = os.path.join(root, fn)
            rel = os.path.relpath(full, PKG)
            z.write(full, rel)
print("安装包已生成:", OUT)
