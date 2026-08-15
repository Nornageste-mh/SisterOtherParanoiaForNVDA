# -*- coding: utf-8 -*-
"""稳定性监控：周期性检查游戏响应性，记录 clickable 事件与扫描耗时。"""
import subprocess
import sys
import time

GAME_LOG = r"F:\Steam\steamapps\common\Sister Other Paranoia\BepInEx\LogOutput.log"
NVDA_LOG = r"C:\Users\Nornageste-mh\AppData\Local\Temp\nvda.log"
DURATION = int(sys.argv[1]) if len(sys.argv) > 1 else 720

def ps_responding():
    out = subprocess.run(
        ["powershell", "-NoProfile", "-Command",
         "$p = Get-Process SisterOtherParanoia -ErrorAction SilentlyContinue; if ($p) { \"$($p.Id)|$($p.Responding)\" } else { 'GONE' }"],
        capture_output=True, text=True, timeout=20,
    )
    return out.stdout.strip()

def tail(path, n=6):
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as f:
            lines = f.readlines()
        return lines[-n:]
    except OSError:
        return []

seen_scan = set()
start = time.time()
last_ok = time.time()
while time.time() - start < DURATION:
    state = ps_responding()
    if state == "GONE":
        print("[%s] 游戏进程消失！" % time.strftime("%H:%M:%S"), flush=True)
        break
    pid, resp = state.split("|")
    if resp != "True":
        print("[%s] 游戏无响应！ PID=%s" % (time.strftime("%H:%M:%S"), pid), flush=True)
    for line in tail(GAME_LOG, 200):
        if "scan took" in line and line not in seen_scan:
            seen_scan.add(line)
            print("[%s] %s" % (time.strftime("%H:%M:%S"), line.strip()), flush=True)
    for line in tail(NVDA_LOG, 200):
        if "可点击目标" in line and line not in seen_scan:
            seen_scan.add(line)
            print("[%s] %s" % (time.strftime("%H:%M:%S"), line.strip()[:120]), flush=True)
    time.sleep(20)
print("监控结束（运行 %d 分钟，游戏仍在: %s）" % ((time.time() - start) / 60, ps_responding()), flush=True)
