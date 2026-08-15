# -*- coding: utf-8 -*-
"""SopAccess 事件流调试监听器：监听 127.0.0.1:42137，把收到的 JSON 行写入文件并打印。"""
import json
import socket
import sys
import time

PORT = 42137
OUT = sys.argv[1] if len(sys.argv) > 1 else r"D:\Harness工作区\sop-nvda\dist\events-debug.log"

def main():
    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind(("127.0.0.1", PORT))
    srv.listen(4)
    print("listening on 127.0.0.1:%d" % PORT, flush=True)
    with open(OUT, "a", encoding="utf-8") as f:
        while True:
            try:
                conn, addr = srv.accept()
            except OSError:
                break
            print("connection from %s" % (addr,), flush=True)
            f.write("=== connection %s ===\n" % (time.strftime("%H:%M:%S"),))
            f.flush()
            buf = b""
            try:
                with conn:
                    conn.settimeout(0.5)
                    while True:
                        try:
                            chunk = conn.recv(65536)
                        except socket.timeout:
                            continue
                        except OSError:
                            break
                        if not chunk:
                            break
                        buf += chunk
                        while b"\n" in buf:
                            line, buf = buf.split(b"\n", 1)
                            line = line.strip()
                            if not line:
                                continue
                            ts = time.strftime("%H:%M:%S")
                            print("[%s] %s" % (ts, line.decode("utf-8", "replace")), flush=True)
                            f.write("[%s] %s\n" % (ts, line.decode("utf-8", "replace")))
                            f.flush()
            except Exception:
                pass
            print("connection closed", flush=True)

if __name__ == "__main__":
    main()
