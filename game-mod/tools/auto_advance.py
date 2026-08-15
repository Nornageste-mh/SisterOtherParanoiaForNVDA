# -*- coding: utf-8 -*-
"""自动推进游戏并记录里程碑事件（选项、读心输入、心声、已选择）。"""
import ctypes
import subprocess
import sys
import time

LOG = r"C:\Users\Nornageste-mh\AppData\Local\Temp\nvda.log"
MILESTONES = ["选项：", "读心输入", "心声：", "已选择", "已确认输入"]
DURATION = int(sys.argv[1]) if len(sys.argv) > 1 else 300
INTERVAL = float(sys.argv[2]) if len(sys.argv) > 2 else 5.0

def send_key(key):
    ps = (
        "$ws = New-Object -ComObject WScript.Shell;"
        "$p = Get-Process SisterOtherParanoia;"
        "$ws.AppActivate($p.Id) | Out-Null;"
        "Start-Sleep -Milliseconds 300;"
        "[System.Windows.Forms.SendKeys]::SendWait('%s')" % key
    )
    subprocess.run(
        ["powershell", "-NoProfile", "-Command",
         "Add-Type -AssemblyName System.Windows.Forms; " + ps],
        capture_output=True, timeout=20,
    )

def read_tail(path, offset):
    with open(path, "r", encoding="utf-8", errors="replace") as f:
        f.seek(offset)
        data = f.read()
        pos = f.tell()
    return data, pos

offset = 0
try:
    with open(LOG, "r", encoding="utf-8", errors="replace") as f:
        f.seek(0, 2)
        offset = f.tell()
except OSError:
    pass

start = time.time()
last_key = 0
while time.time() - start < DURATION:
    data, offset = read_tail(LOG, offset)
    if data:
        for line in data.splitlines():
            for m in MILESTONES:
                if m in line:
                    idx = line.find("朗读 -> ")
                    if idx >= 0:
                        print("[%s] %s" % (time.strftime("%H:%M:%S"), line[idx + len("朗读 -> "):].strip()), flush=True)
    # 每 INTERVAL 秒推进一次
    if time.time() - last_key >= INTERVAL:
        send_key("{ENTER}")
        last_key = time.time()
    time.sleep(0.5)
print("done", flush=True)
