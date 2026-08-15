# -*- coding: utf-8 -*-
"""sopAccess — 《妹妹、他人、妄想症》(Sister Other Paranoia) NVDA 无障碍辅助插件。

通过本机 TCP (127.0.0.1:42137) 接收游戏内 BepInEx 插件（SopAccess）推送的事件：
对话、心声（读心）、选项、界面面板、输入框回显、游戏状态等，并以语音播报，
同时提供对话历史浏览、选项复读、静音开关等快捷键。
"""

from __future__ import annotations

import time
import wx
from collections import deque
from logHandler import log

import config
import globalPluginHandler
import ui
import speech
from speech import Spri
from scriptHandler import script

from . import names as names_mod
from . import server as server_mod
from .settings_panel import SopSettingsPanel, SopSettingsDialog, setPlugin

# 配置定义（ConfigObj spec）
config.conf.spec["sopAccess"] = {
    "port": "integer(default=42137)",
    "speakDialogue": "boolean(default=true)",
    "speakThoughts": "boolean(default=true)",
    "speakChoices": "boolean(default=true)",
    "speakUI": "boolean(default=true)",
    "speakInput": "boolean(default=true)",
    "speakModes": "boolean(default=true)",
    "speakerFormat": "string(default='{author}：{text}')",
    "thoughtPrefix": "string(default='心声：')",
    "historySize": "integer(default=3000)",
    "inputEchoDelay": "float(default=0.35)",
    "clickOffset": "integer(default=2)",
    "debugLog": "boolean(default=false)",
}

# 兼容旧配置：nvda.ini 里已保存的旧配置缺少新增键，运行时访问会抛 KeyError，
# 这里在导入时主动补齐默认值。
_CONFIG_DEFAULTS = {
    "port": 42137,
    "speakDialogue": True,
    "speakThoughts": True,
    "speakChoices": True,
    "speakUI": True,
    "speakInput": True,
    "speakModes": True,
    "speakerFormat": "{author}：{text}",
    "thoughtPrefix": "心声：",
    "historySize": 3000,
    "inputEchoDelay": 0.35,
    "clickOffset": 2,
    "debugLog": False,
}

try:
    _sopConf = config.conf["sopAccess"]
    for _k, _v in _CONFIG_DEFAULTS.items():
        try:
            _sopConf[_k]
        except KeyError:
            _sopConf[_k] = _v
except Exception:
    pass


def describe_position(x, y):
    """把归一化屏幕坐标（Unity：y 自下而上）描述为中文方位。"""
    try:
        x = float(x)
        y = float(y)
    except (TypeError, ValueError):
        return "位置未知"
    h = "左侧" if x < 0.33 else ("中间" if x < 0.66 else "右侧")
    v = "下方" if y < 0.33 else ("中间" if y < 0.66 else "上方")
    return "%s%s，百分之%d、百分之%d" % (h, v, round(x * 100), round(y * 100))


# 菜单类面板：其按钮列表在界面播报中朗读；其余面板的交互对象由“可点击位置”播报
MENU_PANELS = {
    "TitleMenu", "ControlPanelUI", "SettingsMenu", "SettingsUI",
    "SaveLoadMenu", "SaveLoadUI", "BacklogPanel", "RollbackUI",
    "CustomCGGalleryPanel", "SoundGalleryPanel", "TipsUI",
    "PauseUI", "IPauseUI", "HiyakawaInputUI",
}


class GlobalPlugin(globalPluginHandler.GlobalPlugin):

    scriptCategory = "妹妹、他人、妄想症辅助"

    def __init__(self):
        super().__init__()
        self._conf = config.conf["sopAccess"]
        self._muted = False
        self._connected = False
        self._announcedScripts = set()
        self._history = deque(maxlen=int(self._conf["historySize"]))
        self._histIndex = -1
        self._lastDialogue = None  # (author, text)
        self._lastChoices = []  # list[str]
        self._lastPicked = None
        self._lastInputSpoken = None
        self._lastInputTime = 0.0
        self._targets = []  # 可点击目标 [{"label","x","y"}]
        self._targetIndex = -1

        self._server = server_mod.EventServer(self._getPort(), self._onEvent)
        self._server.start()
        self._registerSettingsPanel()
        setPlugin(self)
        self._settingsDialog = None
        self._menuItem = None
        wx.CallAfter(self._registerMenu)
        log.info("sopAccess: 插件已初始化，监听端口 %d", self._getPort())

    # ---------------------------------------------------------------- 配置

    def _getPort(self):
        return int(self._conf["port"])

    def _registerSettingsPanel(self):
        try:
            from gui import settingsDialogs
            if SopSettingsPanel not in settingsDialogs.NVDASettingsDialog.categoryClasses:
                settingsDialogs.NVDASettingsDialog.categoryClasses.append(SopSettingsPanel)
        except Exception:
            log.exception("sopAccess: 注册设置面板失败")

    def _registerMenu(self):
        """在 NVDA 菜单（选项 → 妹妹、他人、妄想症 辅助设置...）注册入口。"""
        try:
            import gui
            frame = gui.mainFrame
            if frame is None or frame.sysTrayIcon is None:
                log.warning("sopAccess: mainFrame 未就绪，稍后重试菜单注册")
                wx.CallLater(2000, self._registerMenu)
                return
            menu = frame.sysTrayIcon.preferencesMenu
            if menu is None:
                log.warning("sopAccess: preferencesMenu 不可用")
                return
            self._menuItem = menu.Append(wx.ID_ANY, "妹妹、他人、妄想症 辅助设置...")
            menu.Bind(wx.EVT_MENU, self._onMenuOpenSettings, self._menuItem)
            log.info("sopAccess: 菜单入口已注册")
        except Exception:
            log.exception("sopAccess: 注册菜单失败")

    def _onMenuOpenSettings(self, evt):
        try:
            import gui
            # 销毁旧实例，避免对话框残留导致无法再次打开
            if self._settingsDialog is not None:
                try:
                    self._settingsDialog.Destroy()
                except Exception:
                    pass
                self._settingsDialog = None
            dlg = SopSettingsDialog(gui.mainFrame)

            def _onClose(_evt):
                try:
                    dlg.Destroy()
                except Exception:
                    pass
                self._settingsDialog = None

            dlg.Bind(wx.EVT_CLOSE, _onClose)
            self._settingsDialog = dlg
            dlg.Show()
        except Exception:
            log.exception("sopAccess: 打开设置窗口失败")

    def restartServer(self):
        """端口变更后重启监听线程。"""
        try:
            self._server.stop()
        except Exception:
            pass
        self._server = server_mod.EventServer(self._getPort(), self._onEvent)
        self._server.start()

    def sendConfig(self):
        """把点击相关配置同步给游戏插件（连接后/设置变更时调用）。"""
        try:
            self._server.sendToGame({
                "t": "config",
                "clickOffset": int(self._conf["clickOffset"]),
            })
        except Exception:
            log.exception("sopAccess: 发送配置失败")

    # ---------------------------------------------------------------- 事件入口

    def _onEvent(self, ev):
        """在 NVDA 主线程上处理游戏事件（由服务器线程 wx.CallAfter 调度）。"""
        t = ev.get("t")
        try:
            if t == "hello":
                self._onHello(ev)
            elif t == "dialogue":
                self._onDialogue(ev)
            elif t == "thought":
                self._onThought(ev)
            elif t == "choice":
                self._onChoice(ev)
            elif t == "picked":
                self._onPicked(ev)
            elif t == "input":
                self._onInput(ev)
            elif t == "inputSubmit":
                self._onInputSubmit(ev)
            elif t == "ui":
                self._onUI(ev)
            elif t == "focus":
                self._onFocus(ev)
            elif t == "clickable":
                self._onClickable(ev)
            elif t == "clickResult":
                self._onClickResult(ev)
            elif t == "scriptDumped":
                self._onScriptDumped(ev)
            elif t == "play":
                self._onPlay(ev)
            elif t == "skip":
                self._onMode("skip", ev)
            elif t == "auto":
                self._onMode("auto", ev)
            # "stop"、"wait"、"uiHide" 等事件按需静默处理
        except Exception:
            log.exception("sopAccess: 处理事件失败 %r", ev)

    def _speak(self, text, priority=Spri.NOW):
        if not text:
            return
        log.info("sopAccess: 朗读 -> %s", text[:120])
        speech.speakText(text, priority=priority)

    def _addHistory(self, kind, text):
        self._history.append((kind, text))
        self._histIndex = -1

    # ---------------------------------------------------------------- 事件处理

    def _onHello(self, ev):
        was = self._connected
        self._connected = True
        if not was and not self._muted and self._conf["speakUI"]:
            self._speak("《妹妹、他人、妄想症》辅助已连接")
        # 连接建立后同步点击配置
        self.sendConfig()

    def _onDialogue(self, ev):
        text = (ev.get("text") or "").strip()
        if not text:
            return
        author = (ev.get("author") or "").strip()
        self._lastDialogue = (author, text)
        self._addHistory("dialogue", self._formatLine(author, text))
        if self._muted or not self._conf["speakDialogue"]:
            return
        self._speak(self._formatLine(author, text))

    def _formatLine(self, author, text):
        if author:
            fmt = str(self._conf["speakerFormat"])
            try:
                return fmt.format(author=author, text=text)
            except Exception:
                return "%s：%s" % (author, text)
        return text

    def _onThought(self, ev):
        text = (ev.get("text") or "").strip()
        if not text:
            return
        prefix = str(self._conf["thoughtPrefix"])
        self._addHistory("thought", prefix + text)
        if self._muted or not self._conf["speakThoughts"]:
            return
        self._speak(prefix + text)

    def _onChoice(self, ev):
        opts = [o.strip() for o in ev.get("options", []) if o and o.strip()]
        if not opts:
            return
        self._lastChoices = opts
        parts = ["%d、%s" % (i + 1, o) for i, o in enumerate(opts)]
        summary = "；".join(parts)
        self._addHistory("choice", "选项：" + summary)
        if self._muted or not self._conf["speakChoices"]:
            return
        self._speak("选项：" + summary + "。请用上下方向键选择，按回车确认。")

    def _onPicked(self, ev):
        text = (ev.get("text") or "").strip()
        if not text:
            return
        self._lastPicked = text
        self._addHistory("picked", "已选择：" + text)
        if self._muted or not self._conf["speakChoices"]:
            return
        self._speak("已选择：" + text)

    def _onInput(self, ev):
        text = ev.get("text") or ""
        now = time.monotonic()
        delay = float(self._conf["inputEchoDelay"])
        if now - self._lastInputTime < delay:
            return
        if text == self._lastInputSpoken:
            return
        self._lastInputTime = now
        self._lastInputSpoken = text
        if self._muted or not self._conf["speakInput"]:
            return
        if text.strip():
            self._speak(text)

    def _onInputSubmit(self, ev):
        text = (ev.get("text") or "").strip()
        self._lastInputSpoken = None
        if self._muted or not self._conf["speakInput"]:
            return
        if text:
            self._speak("已确认输入：" + text)
        else:
            self._speak("输入完成")

    def _onUI(self, ev):
        panel = (ev.get("panel") or "").strip()
        title = (ev.get("title") or "").strip()
        buttons = [b.strip() for b in ev.get("buttons", []) if b and b.strip()]
        if not buttons:
            buttons = []
        else:
            dedup = []
            for b in buttons:
                if not dedup or dedup[-1] != b:
                    dedup.append(b)
            buttons = dedup

        name = names_mod.panelName(panel)
        if name is None:
            return  # 该面板不播报

        parts = []
        if name:
            parts.append(name)
        if panel == "HiyakawaInputUI" and title and title != panel:
            parts.append(title)  # 输入提示文字
        # 菜单类面板播报按钮列表；场景类面板的交互对象由“可点击位置”播报，避免重复
        if buttons and panel in MENU_PANELS:
            mapped = [names_mod.buttonName(b) for b in buttons]
            parts.append("按钮：" + "、".join(mapped))
        msg = "，".join(parts) + "。"
        self._addHistory("ui", msg)
        if self._muted or not self._conf["speakUI"]:
            return
        self._speak(msg)

    def _onFocus(self, ev):
        label = (ev.get("label") or "").strip()
        if not label:
            return
        label = names_mod.buttonName(label)
        if self._muted or not self._conf["speakUI"]:
            return
        self._speak(label)

    def _onClickable(self, ev):
        items = ev.get("items") or []
        self._targets = [it for it in items if isinstance(it, dict)]
        self._targetIndex = -1
        if not self._targets:
            return
        if self._muted or not self._conf["speakUI"]:
            return
        parts = []
        for i, it in enumerate(self._targets, 1):
            label = names_mod.targetName(it.get("label") or "")
            pos = describe_position(it.get("x", 0.5), it.get("y", 0.5))
            parts.append("%d、%s（%s）" % (i, label, pos))
        summary = "；".join(parts)
        self._addHistory("system", "可点击位置：" + summary)
        n = len(self._targets)
        if n <= 9:
            hint = "按 NVDA 加 Alt 加数字 1 到 %d 直接点击对应位置。" % n
        else:
            hint = "按 NVDA 加 Alt 加右箭头切换，加回车点击。"
        self._speak("可点击位置 %d 个：%s。%s" % (n, summary, hint))

    def _onClickResult(self, ev):
        """点击命中的验证结果：未命中时提醒用户，命中时游戏自身会有反馈。"""
        ok = ev.get("ok") in ("1", 1, True, "true")
        label = names_mod.targetName(ev.get("label") or "")
        if ok:
            return  # 命中：游戏自有音效/画面反馈，不额外播报
        attempts = ev.get("attempts")
        if self._muted or not self._conf["speakUI"]:
            return
        if attempts:
            self._speak("%s未命中，已重试%d次" % (label, attempts))
        else:
            self._speak("%s未命中" % label)

    def _onScriptDumped(self, ev):
        path = (ev.get("path") or "").strip()
        lines = ev.get("lines")
        if self._muted:
            return
        if not path:
            self._speak("当前没有可导出的剧本")
            return
        self._speak("剧本已导出：%s，共%d行" % (path, lines))

    def _onPlay(self, ev):
        scriptName = (ev.get("script") or "").strip()
        if not scriptName or scriptName == "Title" or scriptName.startswith("Title/"):
            return
        if scriptName in self._announcedScripts:
            return
        self._announcedScripts.add(scriptName)
        self._addHistory("system", "剧情开始：" + scriptName)
        if self._muted or not self._conf["speakModes"]:
            return
        self._speak("剧情开始")

    def _onMode(self, kind, ev):
        on = ev.get("on") in ("1", 1, True, "true")
        if kind == "skip":
            label = "快进模式开启" if on else "快进模式关闭"
        else:
            label = "自动播放开启" if on else "自动播放关闭"
        self._addHistory("system", label)
        if self._muted or not self._conf["speakModes"]:
            return
        self._speak(label)

    # ---------------------------------------------------------------- 快捷键

    def _currentHistoryItem(self):
        if not self._history:
            return None
        if self._histIndex < 0 or self._histIndex >= len(self._history):
            self._histIndex = len(self._history) - 1
        return self._history[self._histIndex]

    @script(
        gesture="kb:NVDA+alt+upArrow",
        description="朗读上一句游戏对话",
    )
    def script_prevHistory(self, gesture):
        if not self._history:
            return
        if self._histIndex < 0:
            self._histIndex = len(self._history)
        self._histIndex = max(0, self._histIndex - 1)
        item = self._history[self._histIndex]
        self._speak(item[1])

    @script(
        gesture="kb:NVDA+alt+downArrow",
        description="朗读下一句游戏对话",
    )
    def script_nextHistory(self, gesture):
        if not self._history:
            return
        if self._histIndex < 0 or self._histIndex >= len(self._history) - 1:
            return
        self._histIndex += 1
        item = self._history[self._histIndex]
        self._speak(item[1])

    @script(
        gesture="kb:NVDA+alt+home",
        description="朗读游戏最新一句对话",
    )
    def script_latestHistory(self, gesture):
        item = self._currentHistoryItem()
        if item:
            self._speak(item[1])

    @script(
        gesture="kb:NVDA+alt+h",
        description="打开游戏对话历史窗口",
    )
    def script_openHistory(self, gesture):
        if not self._history:
            self._speak("暂无对话历史")
            return
        lines = []
        for kind, text in self._history:
            lines.append(text)
        ui.browseableMessage("\n".join(lines), "《妹妹、他人、妄想症》对话历史")

    @script(
        gesture="kb:NVDA+alt+s",
        description="复读当前最新一句对话",
    )
    def script_repeatDialogue(self, gesture):
        if self._lastDialogue:
            author, text = self._lastDialogue
            self._speak(self._formatLine(author, text))
        else:
            self._speak("暂无对话")

    @script(
        gesture="kb:NVDA+alt+c",
        description="复读当前选项",
    )
    def script_repeatChoices(self, gesture):
        if not self._lastChoices:
            self._speak("当前没有选项")
            return
        parts = ["%d、%s" % (i + 1, o) for i, o in enumerate(self._lastChoices)]
        self._speak("选项：" + "；".join(parts) + "。")

    @script(
        gesture="kb:NVDA+alt+t",
        description="开关游戏辅助播报",
    )
    def script_toggleMute(self, gesture):
        self._muted = not self._muted
        if self._muted:
            self._speak("游戏辅助播报已静音")
        else:
            self._speak("游戏辅助播报已开启")

    @script(
        gesture="kb:NVDA+alt+rightArrow",
        description="切换到下一个可点击目标",
    )
    def script_nextTarget(self, gesture):
        if not self._targets:
            self._speak("当前没有可点击目标")
            return
        self._targetIndex = (self._targetIndex + 1) % len(self._targets)
        it = self._targets[self._targetIndex]
        label = names_mod.targetName(it.get("label") or "")
        self._speak(
            "目标%d：%s（%s）" % (self._targetIndex + 1, label,
                                describe_position(it.get("x", 0.5), it.get("y", 0.5)))
        )

    @script(
        gesture="kb:NVDA+alt+enter",
        description="点击当前可点击目标",
    )
    def script_clickTarget(self, gesture):
        if not self._targets:
            self._speak("当前没有可点击目标")
            return
        if self._targetIndex < 0:
            self._targetIndex = 0
        idx = self._targetIndex
        it = self._targets[idx]
        ok = self._server.sendToGame({"t": "clickAt", "index": idx})
        label = names_mod.targetName(it.get("label") or "")
        if ok:
            self._speak("已点击目标%d：%s" % (idx + 1, label))
        else:
            self._speak("游戏尚未连接，无法点击")

    @script(
        gesture="kb:NVDA+alt+1",
        description="点击第 1 个可点击位置",
    )
    def script_clickTarget1(self, gesture):
        self._clickTargetNumber(1)

    @script(
        gesture="kb:NVDA+alt+2",
        description="点击第 2 个可点击位置",
    )
    def script_clickTarget2(self, gesture):
        self._clickTargetNumber(2)

    @script(
        gesture="kb:NVDA+alt+3",
        description="点击第 3 个可点击位置",
    )
    def script_clickTarget3(self, gesture):
        self._clickTargetNumber(3)

    @script(
        gesture="kb:NVDA+alt+4",
        description="点击第 4 个可点击位置",
    )
    def script_clickTarget4(self, gesture):
        self._clickTargetNumber(4)

    @script(
        gesture="kb:NVDA+alt+5",
        description="点击第 5 个可点击位置",
    )
    def script_clickTarget5(self, gesture):
        self._clickTargetNumber(5)

    @script(
        gesture="kb:NVDA+alt+6",
        description="点击第 6 个可点击位置",
    )
    def script_clickTarget6(self, gesture):
        self._clickTargetNumber(6)

    @script(
        gesture="kb:NVDA+alt+7",
        description="点击第 7 个可点击位置",
    )
    def script_clickTarget7(self, gesture):
        self._clickTargetNumber(7)

    @script(
        gesture="kb:NVDA+alt+8",
        description="点击第 8 个可点击位置",
    )
    def script_clickTarget8(self, gesture):
        self._clickTargetNumber(8)

    @script(
        gesture="kb:NVDA+alt+9",
        description="点击第 9 个可点击位置",
    )
    def script_clickTarget9(self, gesture):
        self._clickTargetNumber(9)

    def _clickTargetNumber(self, num):
        if not self._targets:
            self._speak("当前没有可点击目标")
            return
        if num < 1 or num > len(self._targets):
            self._speak("只有 %d 个可点击位置" % len(self._targets))
            return
        idx = num - 1
        it = self._targets[idx]
        ok = self._server.sendToGame({"t": "clickAt", "index": idx})
        label = names_mod.targetName(it.get("label") or "")
        if ok:
            self._speak("已点击位置%d：%s" % (num, label))
        else:
            self._speak("游戏尚未连接，无法点击")

    @script(
        gesture="kb:NVDA+alt+g",
        description="导出当前场景剧本到临时文件夹",
    )
    def script_dumpScript(self, gesture):
        ok = self._server.sendToGame({"t": "dumpScript"})
        if ok:
            self._speak("正在导出当前剧本")
        else:
            self._speak("游戏尚未连接，无法导出")

    @script(
        gesture="kb:NVDA+alt+f",
        description="导出当前场景可点击对象诊断",
    )
    def script_dumpScene(self, gesture):
        ok = self._server.sendToGame({"t": "dumpScene"})
        if ok:
            self._speak("正在导出场景诊断")
        else:
            self._speak("游戏尚未连接，无法导出")

    # ---------------------------------------------------------------- 生命周期

    def terminate(self):
        try:
            if self._menuItem is not None:
                try:
                    import gui
                    if gui.mainFrame is not None and gui.mainFrame.sysTrayIcon is not None:
                        menu = gui.mainFrame.sysTrayIcon.preferencesMenu
                        if menu is not None:
                            menu.Remove(self._menuItem)
                except Exception:
                    pass
        except Exception:
            pass
        try:
            self._server.stop()
        except Exception:
            pass
        log.info("sopAccess: 插件已卸载")
