# sop-nvda

一个面向视觉小说的 NVDA 辅助插件（游戏内 BepInEx 插件 + NVDA 全局插件）。

> ⚠️ **免责声明**
>
> 本项目是 **Vibe coding 产物**（AI 辅助生成），**非官方**、与游戏开发商无关。
> 代码可能存在各种问题（稳定性、兼容性、安全性等），**不保证可用**，无维护承诺。
> 使用风险自负；仅供个人学习/研究参考，请勿用于商业或分发牟利。
> 建议自行审阅代码后再使用。

## 组成

- `nvda-addon/` — NVDA 全局插件（Python）：对话朗读、可点击位置播报与点击辅助、历史记录、设置窗口等
- `game-mod/` — 游戏内插件（C#，BepInEx 5）：挂钩 Naninovel 引擎，通过本机 TCP 与 NVDA 插件通信

## 安装

1. 游戏侧：将 `winhttp.dll`、`doorstop_config.ini`、`.doorstop_version`、`BepInEx\` 复制到游戏根目录，
   并把 `SopAccess.dll` 放入 `BepInEx\plugins\`（也可运行 `deploy.ps1` 自动部署）
2. NVDA 侧：安装 `sopAccess-*.nvda-addon`（用 `nvda-addon/build_addon.py` 打包生成），重启 NVDA
3. 先启动 NVDA，再启动游戏

## 主要快捷键

| 快捷键 | 功能 |
| --- | --- |
| `NVDA+Alt+1~9` | 直接点击第 1~9 个可点击位置 |
| `NVDA+Alt+右箭头` / `NVDA+Alt+回车` | 切换 / 点击可点击位置 |
| `NVDA+Alt+上/下箭头` | 朗读上/下一句对话 |
| `NVDA+Alt+H` | 打开对话历史 |
| `NVDA+Alt+S` / `NVDA+Alt+C` | 复读对话 / 复读选项 |
| `NVDA+Alt+T` | 静音 / 恢复播报 |
| `NVDA+Alt+G` / `NVDA+Alt+F` | 导出当前剧本 / 场景诊断（存于 %TEMP%） |

## 已知问题 / 注意事项

- 本插件通过读取游戏运行时信息工作，游戏更新后可能失效
- 点击辅助基于游戏自身的输入管道模拟，部分控件可能无法命中
- 未覆盖所有界面与交互；语音朗读依赖 NVDA 配置的合成器（中文需中文语音）
- 安装/卸载不影响游戏原始文件与存档

## 构建

- 游戏插件：`dotnet build game-mod/SopAccess/SopAccess.csproj -c Release`
- NVDA 插件：`python nvda-addon/build_addon.py`
