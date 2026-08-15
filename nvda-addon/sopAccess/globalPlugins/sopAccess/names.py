# -*- coding: utf-8 -*-
"""面板与按钮的中文名称映射。"""

from __future__ import annotations

import re

# 面板类型名 -> 中文名；值为 None 表示不播报
PANEL_NAMES = {
    "TitleMenu": "标题画面",
    "ControlPanelUI": "游戏控制面板",
    "SettingsMenu": "设置菜单",
    "SettingsUI": "设置菜单",
    "SaveLoadMenu": "存档与读档",
    "SaveLoadUI": "存档与读档",
    "BacklogPanel": "对话历史",
    "RollbackUI": "对话历史",
    "CGalleryUI": "CG画廊",
    "CustomCGGalleryPanel": "CG画廊",
    "SoundGalleryPanel": "音声画廊",
    "TipsUI": "提示",
    "HiyakawaInputUI": "读心输入",
    "CreditsVideoUI": "职员表",
    "PauseUI": "暂停菜单",
    "IPauseUI": "暂停菜单",
    "LoadingPanel": None,
    "ToastUI": None,
    "ClickThroughPanel": None,
    "ContinueInputUI": None,
    "SceneTransitionUI": None,
    "ScriptNavigatorPanel": None,
    "ExternalScriptsBrowserPanel": None,
    "VariableInputPanel": None,
    "VirtualMouseUI": None,
    "CustomUI": None,
    # 场景类面板（交互对象由“可点击位置”播报，此处只报面板名）
    "SmartPhone_CG_UI": "手机界面",
    "Circle_UI": "社团选择",
    "OP_Store_UI": "商店",
    "Sister_CG1_Search": "搜查界面",
    "Sister_CG2_Search": "搜查界面",
}

# 按钮对象名 -> 中文名（悬停才显示文字的按钮没有静态文本，按对象名映射）
BUTTON_NAMES = {
    "NewGameButton": "新游戏",
    "ContinueButton": "继续游戏",
    "GalleryButton": "画廊",
    "SettingsButton": "设置",
    "ExitButton": "退出游戏",
    "SoundGalleryButton": "音声画廊",
    "TipsButton": "提示",
    "ExternalScriptsButton": "外部脚本",
    "TitleCGGalleryButton": "画廊",
    "TitleSoundGalleryButton": "音声画廊",
    "TitleSettingsButton": "设置",
    "Object1": "宣传按钮一",
    "GrandEnd": "宣传按钮二",
    "BacklogButton": "历史记录",
    "SaveButton": "保存",
    "LoadButton": "读取",
    "AutoPlayButton": "自动播放",
    "SkipButton": "快进",
    "SettingsButton2": "设置",
    "ReturnButton": "返回",
    "BackButton": "返回",
    "CloseButton": "关闭",
    "OkButton": "确定",
    "OKButton": "确定",
    "ConfirmButton": "确定",
    "CancelButton": "取消",
    "ClearButton": "清除",
    "DeleteButton": "删除",
    "CopyButton": "复制",
    "PlayButton": "播放",
    "StopButton": "停止",
    "PrevButton": "上一个",
    "NextButton": "下一个",
    "PageUpButton": "上一页",
    "PageDownButton": "下一页",
    "FullscreenButton": "全屏",
    "LanguageButton": "语言",
    "VolumeButton": "音量",
    "BgmButton": "背景音乐",
    "VoiceButton": "语音",
    "SfxButton": "音效",
    "TextSpeedButton": "文字速度",
    "AutoButton": "自动",
    "SkipAllButton": "全部快进",
    "ReadAloudButton": "朗读",
    "MainMenuButton": "主菜单",
    "TitleButton": "返回标题",
    "RestartButton": "重新开始",
    "RetryButton": "重试",
    "YesButton": "是",
    "NoButton": "否",
    "ConfirmQuitButton": "确认退出",
}

_CAMEL_RE = re.compile(r"(?<=[a-z0-9])(?=[A-Z])")


def panelName(panelId):
    """返回面板中文名；None 表示忽略该面板。"""
    if panelId in PANEL_NAMES:
        return PANEL_NAMES[panelId]
    # 未知面板：去除常见后缀后按原样播报
    return panelId


# 场景可点击目标/对象名 -> 中文名（游戏场景热点）
TARGET_NAMES = {
    "mobm1": "男同学一",
    "mobm2": "男同学二",
    "mobg1": "女同学一",
    "mobg2": "女同学二",
    "hiyakawa": "冷川学长",
    "oyaji": "大叔店主",
    "Oyaji": "大叔店主",
    "tachiyomi": "站立阅读",
    "Tachiyomi": "站立阅读",
    "kyaba": "夜店",
    "Kyaba": "夜店",
}


def targetName(raw):
    """可点击目标的名称：优先中文映射，其次 ObjectN -> 目标N，再次去掉数字前缀，最后通用转换。"""
    if raw in TARGET_NAMES:
        return TARGET_NAMES[raw]
    if raw in BUTTON_NAMES:
        return BUTTON_NAMES[raw]
    m = re.match(r"^Object\s*\(?\s*(\d+)\s*\)?$", raw, re.IGNORECASE)
    if m:
        return "目标" + m.group(1)
    n = re.sub(r"^\d+\s*[.、:：]\s*", "", raw)  # 去掉 "9. " 之类的序号前缀
    if n and n != raw:
        return n
    return buttonName(raw)


def buttonName(raw):
    """把按钮标签/对象名转换为可读中文。"""
    if raw in BUTTON_NAMES:
        return BUTTON_NAMES[raw]
    n = raw
    if n.endswith("Button"):
        n = n[:-6]
    n = _CAMEL_RE.sub(" ", n)
    n = n.replace("_", " ").replace("-", " ").strip()
    return n if n else raw
