# -*- coding: utf-8 -*-
"""测试探针：向 42137 注入测试事件，验证 NVDA 插件朗读链路。"""
import json
import socket
import time

s = socket.create_connection(("127.0.0.1", 42137), timeout=5)
events = [
    {"t": "dialogue", "text": "这是一条来自测试探针的对话。", "author": "测试员"},
    {"t": "choice", "options": ["去上学", "留在家里"]},
    {"t": "ui", "panel": "TitleMenu", "title": "TitleUI",
     "buttons": ["NewGameButton", "ContinueButton", "SettingsButton"]},
    {"t": "thought", "text": "这个人心里在盘算什么。", "emotion": "fear"},
    {"t": "input", "text": "沙华"},
    {"t": "inputSubmit", "text": "沙华"},
]
for ev in events:
    s.sendall((json.dumps(ev, ensure_ascii=False) + "\n").encode("utf-8"))
    time.sleep(0.4)
s.close()
print("probe sent", flush=True)
