# -*- coding: utf-8 -*-
"""TCP 事件服务器：监听 127.0.0.1:<port>，接收游戏插件推送的 JSON 行并回调。"""

from __future__ import annotations

import json
import socket
import threading

import wx
from logHandler import log


class EventServer(threading.Thread):

    def __init__(self, port, onEvent):
        super().__init__(daemon=True, name="sopAccess-server")
        self._port = port
        self._onEvent = onEvent
        self._stop = threading.Event()
        self._gameConn = None
        self._gameLock = threading.Lock()

    def stop(self):
        self._stop.set()

    def sendToGame(self, obj):
        """向游戏插件发送命令（仅发给第一条连接，即游戏）。"""
        with self._gameLock:
            conn = self._gameConn
        if conn is None:
            return False
        try:
            conn.sendall((json.dumps(obj, ensure_ascii=False) + "\n").encode("utf-8"))
            return True
        except OSError:
            return False

    def run(self):
        log.info("sopAccess-server: 线程启动，端口 %d", self._port)
        while not self._stop.is_set():
            try:
                srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
                srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
                srv.bind(("127.0.0.1", self._port))
                srv.listen(8)
                srv.settimeout(0.5)
                log.info("sopAccess-server: 开始监听 %d", self._port)
                try:
                    while not self._stop.is_set():
                        try:
                            conn, _addr = srv.accept()
                        except socket.timeout:
                            continue
                        except OSError:
                            break
                        log.info("sopAccess-server: 收到连接")
                        # 每个连接独立线程处理，避免单个长连接阻塞其它连接
                        t = threading.Thread(
                            target=self._handle_connection,
                            args=(conn,),
                            daemon=True,
                            name="sopAccess-conn",
                        )
                        t.start()
                finally:
                    try:
                        srv.close()
                    except OSError:
                        pass
            except OSError as e:
                log.warning("sopAccess-server: 端口 %d 不可用 (%s)，重试", self._port, e)
                self._stop.wait(5.0)
            except Exception:
                log.exception("sopAccess-server: 未知错误")
                self._stop.wait(5.0)

    def _handle_connection(self, conn):
        buf = b""
        try:
            with self._gameLock:
                if self._gameConn is None:
                    self._gameConn = conn  # 第一条连接视为游戏
            with conn:
                conn.settimeout(0.5)
                while not self._stop.is_set():
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
                        log.debug("sopAccess-server: 收到事件行: %s", line[:160])
                        try:
                            ev = json.loads(line.decode("utf-8", "replace"))
                        except Exception:
                            continue
                        try:
                            wx.CallAfter(self._onEvent, ev)
                        except Exception:
                            log.exception("sopAccess-server: wx.CallAfter 失败")
        except Exception:
            log.exception("sopAccess-server: 连接处理异常")
        finally:
            with self._gameLock:
                if self._gameConn is conn:
                    self._gameConn = None
