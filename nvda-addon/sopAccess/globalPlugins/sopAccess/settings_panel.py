# -*- coding: utf-8 -*-
"""《妹妹、他人、妄想症》辅助设置：NVDA 设置面板 + 独立设置对话框（NVDA 菜单唤出）。"""

from __future__ import annotations

import wx

import config
from gui import guiHelper
from gui.settingsDialogs import SettingsPanel
from logHandler import log

# 由全局插件在初始化时注册，用于端口变更后重启监听
_plugin = None


def setPlugin(plugin):
    global _plugin
    _plugin = plugin


def _buildControls(parent, helper, values):
    """在 BoxSizerHelper 中创建全部设置控件，返回控件字典。"""
    ctrls = {}
    ctrls["port"] = helper.addLabeledControl(
        "监听端口（需与游戏插件一致）：",
        wx.TextCtrl,
        value=str(values["port"]),
    )
    ctrls["speakDialogue"] = helper.addItem(wx.CheckBox(parent, label="朗读对话"))
    ctrls["speakDialogue"].SetValue(bool(values["speakDialogue"]))
    ctrls["speakThoughts"] = helper.addItem(wx.CheckBox(parent, label="播报心声（读心内容）"))
    ctrls["speakThoughts"].SetValue(bool(values["speakThoughts"]))
    ctrls["speakChoices"] = helper.addItem(wx.CheckBox(parent, label="播报选项与选择结果"))
    ctrls["speakChoices"].SetValue(bool(values["speakChoices"]))
    ctrls["speakUI"] = helper.addItem(wx.CheckBox(parent, label="播报菜单与界面按钮"))
    ctrls["speakUI"].SetValue(bool(values["speakUI"]))
    ctrls["speakInput"] = helper.addItem(wx.CheckBox(parent, label="回显读心输入内容"))
    ctrls["speakInput"].SetValue(bool(values["speakInput"]))
    ctrls["speakModes"] = helper.addItem(wx.CheckBox(parent, label="播报快进/自动播放等模式切换"))
    ctrls["speakModes"].SetValue(bool(values["speakModes"]))

    ctrls["speakerFormat"] = helper.addLabeledControl(
        "说话人格式（{author} 为角色名，{text} 为内容）：",
        wx.TextCtrl,
        value=str(values["speakerFormat"]),
    )
    ctrls["thoughtPrefix"] = helper.addLabeledControl(
        "心声前缀：",
        wx.TextCtrl,
        value=str(values["thoughtPrefix"]),
    )
    ctrls["historySize"] = helper.addLabeledControl(
        "对话历史条数：",
        wx.TextCtrl,
        value=str(values["historySize"]),
    )
    ctrls["inputEchoDelay"] = helper.addLabeledControl(
        "输入回显间隔（秒）：",
        wx.TextCtrl,
        value=str(values["inputEchoDelay"]),
    )
    # 点空偏移激进度：点击落空时向四周搜索不透明像素的强度
    ctrls["clickOffsetLabel"] = helper.addItem(
        wx.StaticText(parent, label="点空偏移激进度（点击落空时的搜索强度）：")
    )
    ctrls["clickOffset"] = helper.addItem(
        wx.Choice(parent, choices=["关闭", "温和", "标准", "激进"])
    )
    try:
        level = int(values["clickOffset"])
    except (TypeError, ValueError):
        level = 2
    ctrls["clickOffset"].SetSelection(max(0, min(3, level)))
    return ctrls


def _safeInt(conf, key, default):
    try:
        return int(conf[key])
    except (KeyError, TypeError, ValueError):
        return default


def _safeFloat(conf, key, default):
    try:
        return float(conf[key])
    except (KeyError, TypeError, ValueError):
        return default


def _applyAndSave(ctrls, oldPort):
    """读取控件值写入配置；端口变更时重启监听。返回是否发生端口变更。"""
    conf = config.conf["sopAccess"]

    try:
        port = int(ctrls["port"].GetValue())
    except (TypeError, ValueError):
        port = oldPort
    if port < 1 or port > 65535:
        port = oldPort

    try:
        historySize = int(ctrls["historySize"].GetValue())
    except (TypeError, ValueError):
        historySize = _safeInt(conf, "historySize", DEFAULTS["historySize"])
    if historySize < 10:
        historySize = 10

    try:
        echoDelay = float(ctrls["inputEchoDelay"].GetValue())
    except (TypeError, ValueError):
        echoDelay = _safeFloat(conf, "inputEchoDelay", DEFAULTS["inputEchoDelay"])
    if echoDelay < 0.0:
        echoDelay = 0.0

    conf["port"] = port
    conf["speakDialogue"] = bool(ctrls["speakDialogue"].GetValue())
    conf["speakThoughts"] = bool(ctrls["speakThoughts"].GetValue())
    conf["speakChoices"] = bool(ctrls["speakChoices"].GetValue())
    conf["speakUI"] = bool(ctrls["speakUI"].GetValue())
    conf["speakInput"] = bool(ctrls["speakInput"].GetValue())
    conf["speakModes"] = bool(ctrls["speakModes"].GetValue())
    conf["speakerFormat"] = ctrls["speakerFormat"].GetValue()
    conf["thoughtPrefix"] = ctrls["thoughtPrefix"].GetValue()
    conf["historySize"] = historySize
    conf["inputEchoDelay"] = echoDelay
    conf["clickOffset"] = max(0, min(3, ctrls["clickOffset"].GetSelection()))

    # 配置同步到游戏插件（点击偏移等）
    if _plugin is not None:
        try:
            _plugin.sendConfig()
        except Exception:
            log.exception("sopAccess: 同步配置失败")

    if port != oldPort:
        log.info("sopAccess: 端口变更 %d -> %d，重启监听", oldPort, port)
        if _plugin is not None:
            try:
                _plugin.restartServer()
            except Exception:
                log.exception("sopAccess: 重启监听失败")
        return True
    return False


class SopSettingsPanel(SettingsPanel):

    title = "妹妹、他人、妄想症 辅助"

    def __init__(self, parent):
        super().__init__(parent)
        self._ctrls = None

    def makeSettings(self, settingsSizer):
        sHelper = guiHelper.BoxSizerHelper(self, orientation=wx.VERTICAL)
        self._ctrls = _buildControls(self, sHelper, _currentValues())
        settingsSizer.Add(sHelper.sizer, border=guiHelper.BORDER_FOR_DIALOGS, flag=wx.ALL)

    def onSave(self):
        oldPort = int(config.conf["sopAccess"]["port"])
        _applyAndSave(self._ctrls, oldPort)


# 配置文件操作（导出/从文件恢复/恢复默认）
CONFIG_KEYS = (
    "port", "speakDialogue", "speakThoughts", "speakChoices", "speakUI",
    "speakInput", "speakModes", "speakerFormat", "thoughtPrefix",
    "historySize", "inputEchoDelay", "clickOffset",
)

DEFAULTS = {
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
}


def _currentValues():
    """读取配置，缺失键（旧配置未保存的新增键）回退默认值。
    注意：不能用 conf.get()——ConfigObj 的 get 对 spec 键会抛 KeyError。"""
    conf = config.conf["sopAccess"]
    out = {}
    for k in CONFIG_KEYS:
        try:
            out[k] = conf[k]
        except KeyError:
            out[k] = DEFAULTS[k]
    return out


def _applyDictToConf(data):
    """把字典写入配置（类型按当前值转换，非法值忽略）。"""
    conf = config.conf["sopAccess"]
    for k in CONFIG_KEYS:
        if k not in data:
            continue
        try:
            cur = conf[k]
            if isinstance(cur, bool):
                conf[k] = bool(data[k])
            elif isinstance(cur, int):
                conf[k] = int(data[k])
            elif isinstance(cur, float):
                conf[k] = float(data[k])
            else:
                conf[k] = str(data[k])
        except (TypeError, ValueError, KeyError):
            continue


def _refreshControls(ctrls):
    """从配置刷新控件显示（文件恢复/恢复默认后调用）。"""
    values = _currentValues()
    ctrls["port"].SetValue(str(values["port"]))
    ctrls["speakDialogue"].SetValue(bool(values["speakDialogue"]))
    ctrls["speakThoughts"].SetValue(bool(values["speakThoughts"]))
    ctrls["speakChoices"].SetValue(bool(values["speakChoices"]))
    ctrls["speakUI"].SetValue(bool(values["speakUI"]))
    ctrls["speakInput"].SetValue(bool(values["speakInput"]))
    ctrls["speakModes"].SetValue(bool(values["speakModes"]))
    ctrls["speakerFormat"].SetValue(str(values["speakerFormat"]))
    ctrls["thoughtPrefix"].SetValue(str(values["thoughtPrefix"]))
    ctrls["historySize"].SetValue(str(values["historySize"]))
    ctrls["inputEchoDelay"].SetValue(str(values["inputEchoDelay"]))
    try:
        ctrls["clickOffset"].SetSelection(max(0, min(3, int(values["clickOffset"]))))
    except (TypeError, ValueError):
        ctrls["clickOffset"].SetSelection(2)


class SopSettingsDialog(wx.Dialog):
    """独立的插件设置窗口（由 NVDA 菜单唤出）。"""

    def __init__(self, parent):
        super().__init__(parent, title="妹妹、他人、妄想症 辅助设置")
        mainSizer = wx.BoxSizer(wx.VERTICAL)
        sHelper = guiHelper.BoxSizerHelper(self, orientation=wx.VERTICAL)

        self._ctrls = _buildControls(self, sHelper, _currentValues())

        # 文件操作按钮区
        fileBtns = wx.BoxSizer(wx.HORIZONTAL)
        exportBtn = wx.Button(self, label="导出配置到文件...")
        restoreBtn = wx.Button(self, label="从文件恢复配置...")
        resetBtn = wx.Button(self, label="恢复默认值")
        fileBtns.Add(exportBtn, proportion=1, flag=wx.EXPAND)
        fileBtns.Add(restoreBtn, proportion=1, flag=wx.EXPAND | wx.LEFT, border=8)
        fileBtns.Add(resetBtn, proportion=1, flag=wx.EXPAND | wx.LEFT, border=8)
        self.Bind(wx.EVT_BUTTON, self._onExport, exportBtn)
        self.Bind(wx.EVT_BUTTON, self._onRestore, restoreBtn)
        self.Bind(wx.EVT_BUTTON, self._onReset, resetBtn)
        mainSizer.Add(fileBtns, border=guiHelper.BORDER_FOR_DIALOGS, flag=wx.ALL | wx.EXPAND)

        btns = self.CreateButtonSizer(wx.OK | wx.CANCEL)
        self.Bind(wx.EVT_BUTTON, self._onOk, id=wx.ID_OK)
        mainSizer.Add(sHelper.sizer, border=guiHelper.BORDER_FOR_DIALOGS, flag=wx.ALL)
        mainSizer.Add(btns, border=guiHelper.BORDER_FOR_DIALOGS, flag=wx.ALL | wx.EXPAND)
        self.SetSizer(mainSizer)
        mainSizer.Fit(self)
        self.CentreOnScreen()

    def _onOk(self, evt):
        oldPort = int(config.conf["sopAccess"]["port"])
        try:
            _applyAndSave(self._ctrls, oldPort)
        except Exception:
            log.exception("sopAccess: 保存设置失败")
        self.EndModal(wx.ID_OK)

    def _onExport(self, evt):
        import json
        try:
            with wx.FileDialog(
                self, "导出配置到文件", defaultFile="sopAccess-config.json",
                wildcard="JSON 文件 (*.json)|*.json",
                style=wx.FD_SAVE | wx.FD_OVERWRITE_PROMPT,
            ) as dlg:
                if dlg.ShowModal() != wx.ID_OK:
                    return
                path = dlg.GetPath()
                data = {k: config.conf["sopAccess"][k] for k in CONFIG_KEYS}
                with open(path, "w", encoding="utf-8") as f:
                    json.dump(data, f, ensure_ascii=False, indent=2)
                log.info("sopAccess: 配置已导出到 %s", path)
        except Exception:
            log.exception("sopAccess: 导出配置失败")

    def _onRestore(self, evt):
        import json
        try:
            with wx.FileDialog(
                self, "从文件恢复配置", defaultFile="sopAccess-config.json",
                wildcard="JSON 文件 (*.json)|*.json",
                style=wx.FD_OPEN | wx.FD_FILE_MUST_EXIST,
            ) as dlg:
                if dlg.ShowModal() != wx.ID_OK:
                    return
                path = dlg.GetPath()
                with open(path, "r", encoding="utf-8") as f:
                    data = json.load(f)
                oldPort = int(config.conf["sopAccess"]["port"])
                _applyDictToConf(data)
                _refreshControls(self._ctrls)
                if _plugin is not None:
                    try:
                        _plugin.sendConfig()
                    except Exception:
                        pass
                if int(config.conf["sopAccess"]["port"]) != oldPort and _plugin is not None:
                    try:
                        _plugin.restartServer()
                    except Exception:
                        pass
                log.info("sopAccess: 配置已从 %s 恢复", path)
        except Exception:
            log.exception("sopAccess: 恢复配置失败")

    def _onReset(self, evt):
        try:
            oldPort = int(config.conf["sopAccess"]["port"])
            conf = config.conf["sopAccess"]
            for k, v in DEFAULTS.items():
                conf[k] = v
            _refreshControls(self._ctrls)
            if _plugin is not None:
                try:
                    _plugin.sendConfig()
                except Exception:
                    pass
            if int(conf["port"]) != oldPort and _plugin is not None:
                try:
                    _plugin.restartServer()
                except Exception:
                    pass
            log.info("sopAccess: 配置已恢复默认值")
        except Exception:
            log.exception("sopAccess: 恢复默认值失败")
