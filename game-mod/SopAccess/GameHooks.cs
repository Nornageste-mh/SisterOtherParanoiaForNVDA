// GameHooks — 挂钩 Naninovel 引擎事件，把对话/心声/选项/界面/输入等转换为无障碍事件。
using System;
using System.Collections.Generic;
using Naninovel;
using Naninovel.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using nysec.UI;
using Projects.Background;

namespace SopAccess
{
    public sealed class GameHooks
    {
        private EventSender sender;

        private ITextPrinterManager textPrinter;
        private IScriptPlayer scriptPlayer;
        private IChoiceHandlerManager choiceHandler;
        private IUIManager uiManager;

        private ChoiceHandlerPanel choicePanel;
        private bool choicePanelSubscribed;

        private readonly List<IManagedUI> subscribedUIs = new List<IManagedUI>();
        private readonly Dictionary<IManagedUI, Action<bool>> uiVisibilityHandlers = new Dictionary<IManagedUI, Action<bool>>();

        // 已播报过的可见面板（用于 uiHide 去重与延迟播报）
        private readonly HashSet<string> announcedPanels = new HashSet<string>();
        private readonly Dictionary<string, PendingAnnounce> pendingAnnounces = new Dictionary<string, PendingAnnounce>();

        private List<string> lastOptions = new List<string>();
        private string lastDialogueText = string.Empty;

        // Hiyakawa（读心输入）UI 状态
        private HiyakawaInputUI hiyakawaUI;
        private bool hiyakawaVisible;
        private string hiyakawaLastText = string.Empty;
        private float nextHiyakawaCheck;

        private bool hooked;

        private ClickAssist clickAssist;

        private class PendingAnnounce
        {
            public IManagedUI UI;
            public float AtTime;
        }

        // 无信息量的常驻悬浮层，不播报
        private static readonly HashSet<string> NoisePanels = new HashSet<string>
        {
            "ClickThroughPanel",
            "ContinueInputUI",
            "LoadingPanel",
            "ScriptNavigatorPanel",
            "ExternalScriptsBrowserPanel",
            "ToastUI",
            "VariableInputPanel",
            "SceneTransitionUI",
            "VirtualMouseUI",
            "CustomUI", // 游戏通用装饰性 UI（虚拟鼠标等），其 GameObject 名通常也不具信息量
        };

        public void AttachSender(EventSender s)
        {
            sender = s;
            clickAssist = new ClickAssist(s);
        }

        public void HandleCommand(string type, int index)
        {
            if (clickAssist != null) clickAssist.HandleCommand(type, index);
            if (type == "dumpScript") DumpCurrentScript();
            if (type == "dumpScene") DumpSceneDiagnostics();
        }
        /// <summary>把当前场景所有可点击对象/相机的状态导出到 %TEMP%\sop_scene.txt（诊断用）。</summary>
        private void DumpSceneDiagnostics()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("== 场景可点击对象诊断 ==");
                sb.AppendLine("--- 相机 ---");
                foreach (var c in UnityEngine.Camera.allCameras)
                {
                    if (c == null) continue;
                    sb.AppendLine(string.Format("相机 {0}: enabled={1} pos={2} ortho={3} size={4}",
                        c.name, c.enabled, c.transform.position, c.orthographic, c.orthographicSize));
                }
                var nanoCam = Engine.GetService<ICameraManager>()?.Camera;
                sb.AppendLine("Naninovel 相机: " + (nanoCam != null ? nanoCam.name : "null"));

                sb.AppendLine("--- ClickAdvanceTarget ---");
                foreach (var ct in UnityEngine.Object.FindObjectsOfType<ClickAdvanceTarget>(true))
                {
                    if (ct == null) continue;
                    var col = ct.GetComponentInChildren<Collider2D>(true);
                    string id = "?";
                    if (clickAssist != null) id = clickAssist.GetClickTargetId(ct) ?? "?";
                    sb.AppendLine(string.Format("{0}: active={1} enabled={2} col={3} bounds={4}",
                        ct.name, ct.gameObject.activeInHierarchy, ct.isActiveAndEnabled,
                        col != null ? col.GetType().Name : "NULL",
                        col != null ? col.bounds.center.ToString() : "-"));
                }

                sb.AppendLine("--- WorldScriptTriggerButton ---");
                foreach (var w in UnityEngine.Object.FindObjectsOfType<WorldScriptTriggerButton>(true))
                {
                    if (w == null) continue;
                    var col = w.GetComponentInChildren<Collider2D>(true);
                    sb.AppendLine(string.Format("{0}: active={1} enabled={2} col={3}",
                        w.name, w.gameObject.activeInHierarchy, w.isActiveAndEnabled,
                        col != null ? col.GetType().Name : "NULL"));
                }

                sb.AppendLine("--- StarClickHandler ---");
                foreach (var s in UnityEngine.Object.FindObjectsOfType<StarClickHandler>(true))
                {
                    if (s == null) continue;
                    var col = s.GetComponentInChildren<Collider2D>(true);
                    sb.AppendLine(string.Format("{0}: active={1} enabled={2} col={3}",
                        s.name, s.gameObject.activeInHierarchy, s.isActiveAndEnabled,
                        col != null ? col.GetType().Name : "NULL"));
                }

                sb.AppendLine("--- WorldHoverTrigger ---");
                foreach (var h in UnityEngine.Object.FindObjectsOfType<WorldHoverTrigger>(true))
                {
                    if (h == null) continue;
                    var col = h.GetComponentInChildren<Collider2D>(true);
                    sb.AppendLine(string.Format("{0}: active={1} enabled={2} col={3}",
                        h.name, h.gameObject.activeInHierarchy, h.isActiveAndEnabled,
                        col != null ? col.GetType().Name : "NULL"));
                }

                sb.AppendLine("--- 可见菜单面板 ---");
                try
                {
                    var uim = Engine.GetService<IUIManager>();
                    if (uim != null)
                    {
                        var uis = new List<IManagedUI>();
                        uim.GetManagedUIs(uis);
                        foreach (var ui in uis)
                        {
                            if (ui == null || !ui.Visible) continue;
                            sb.AppendLine(string.Format("{0}: {1}", ui.GetType().Name,
                                (ui as Component) != null ? ((Component)ui).gameObject.name : "-"));
                        }
                    }
                }
                catch { }

                sb.AppendLine("--- 活跃 Button（前 30 个） ---");
                int count = 0;
                foreach (var b in UnityEngine.Object.FindObjectsOfType<Button>())
                {
                    if (b == null || count >= 30) break;
                    if (!b.isActiveAndEnabled) continue;
                    sb.AppendLine(string.Format("{0}: interactable={1} parent={2}",
                        b.gameObject.name, b.interactable,
                        b.transform.parent != null ? b.transform.parent.name : "-"));
                    count++;
                }

                string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sop_scene.txt");
                System.IO.File.WriteAllText(path, sb.ToString(), System.Text.Encoding.UTF8);
                try
                {
                    string snap = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                        "sop_scene_" + System.DateTime.Now.ToString("HHmmss") + ".txt");
                    System.IO.File.WriteAllText(snap, sb.ToString(), System.Text.Encoding.UTF8);
                }
                catch { }
                Plugin.Log.LogInfo("SopAccess: scene diagnostics dumped to " + path);
                sender.SendScriptDumped("场景诊断", sb.Length);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("SopAccess: DumpSceneDiagnostics error: " + e.Message);
            }
        }

        /// <summary>把当前播放的剧本原文（含注释与行号）导出到 %TEMP%\sop_script.txt。</summary>
        private void DumpCurrentScript()
        {
            try
            {
                var sp = Engine.GetService<IScriptPlayer>();
                var script = sp != null ? sp.PlayedScript : null;
                if (script == null || script.Lines == null)
                {
                    sender.SendScriptDumped(null, 0);
                    return;
                }
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("== 剧本: " + script.Path + " ==");
                bool anyLine = false;
                for (int i = 0; i < script.Lines.Count; i++)
                {
                    var line = script.Lines[i];
                    string text = null;
                    try
                    {
                        script.TextMap.Map.TryGetValue(line.LineHash, out text);
                    }
                    catch { }
                    if (string.IsNullOrWhiteSpace(text) && line is Naninovel.GenericTextScriptLine generic)
                    {
                        // transient 剧本无 TextMap：转储行内命令
                        var cmdSb = new System.Text.StringBuilder();
                        foreach (var cmd in generic.InlinedCommands)
                        {
                            if (cmd == null) continue;
                            try
                            {
                                var aliasProp = cmd.GetType().GetProperty("Alias",
                                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                                object alias = aliasProp != null ? aliasProp.GetValue(cmd, null) : null;
                                cmdSb.Append(alias ?? cmd.GetType().Name).Append(' ');
                                var valProp = cmd.GetType().GetProperty("Value",
                                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                                if (valProp != null)
                                {
                                    var v = valProp.GetValue(cmd, null);
                                    if (v != null) cmdSb.Append(v);
                                }
                            }
                            catch { }
                            cmdSb.Append(" | ");
                        }
                        if (cmdSb.Length > 0)
                        {
                            text = cmdSb.ToString().TrimEnd(' ', '|');
                        }
                    }
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    anyLine = true;
                    string comment = null;
                    try { comment = script.GetCommentForLine(i); } catch { }
                    sb.Append((i + 1).ToString().PadLeft(4)).Append(": ").Append(text.Trim());
                    if (!string.IsNullOrWhiteSpace(comment))
                    {
                        sb.Append("    ; ").Append(comment);
                    }
                    sb.AppendLine();
                }
                if (!anyLine)
                {
                    // 兜底：命令级全文提取（含所有参数）
                    sb.AppendLine("--- 命令级转储 ---");
                    try
                    {
                        foreach (var cmd in script.ExtractCommands())
                        {
                            if (cmd == null) continue;
                            var spot = cmd.PlaybackSpot;
                            var aliasProp = cmd.GetType().GetProperty("Alias",
                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            object alias = aliasProp != null ? aliasProp.GetValue(cmd, null) : null;
                            sb.Append(spot.LineNumber.ToString().PadLeft(4)).Append(": @").Append(alias ?? cmd.GetType().Name);
                            foreach (var f in cmd.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                            {
                                if (f == null) continue;
                                try
                                {
                                    var v = f.GetValue(cmd);
                                    if (v == null) continue;
                                    string vs = v.ToString();
                                    if (string.IsNullOrEmpty(vs) || vs == "0" || vs == "False") continue;
                                    sb.Append(' ').Append(f.Name).Append('=').Append(vs);
                                }
                                catch { }
                            }
                            sb.AppendLine();
                        }
                    }
                    catch (Exception e)
                    {
                        sb.AppendLine("命令转储失败: " + e.Message);
                    }
                }

                // 自定义变量表（解谜关键：角色 Living 状态等）
                sb.AppendLine("--- 自定义变量 ---");
                try
                {
                    var vm = Engine.GetService<ICustomVariableManager>();
                    if (vm != null)
                    {
                        foreach (var cv in vm.Variables)
                        {
                            if (cv == null || cv.Name == null) continue;
                            sb.AppendLine(cv.Name + " = " + cv.Value);
                        }
                    }
                    else
                    {
                        sb.AppendLine("(变量管理器不可用)");
                    }
                }
                catch (Exception e)
                {
                    sb.AppendLine("变量读取失败: " + e.Message);
                }
                string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sop_script.txt");
                System.IO.File.WriteAllText(path, sb.ToString(), System.Text.Encoding.UTF8);
                // 同时保留带序号快照，避免多次导出互相覆盖
                try
                {
                    string snap = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                        "sop_script_" + System.DateTime.Now.ToString("HHmmss") + ".txt");
                    System.IO.File.WriteAllText(snap, sb.ToString(), System.Text.Encoding.UTF8);
                }
                catch { }
                Plugin.Log.LogInfo("SopAccess: script dumped to " + path + " (" + script.Lines.Count + " lines)");
                sender.SendScriptDumped(script.Path, script.Lines.Count);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("SopAccess: DumpCurrentScript error: " + e.Message);
            }
        }

        public void Hook()
        {
            if (hooked || sender == null) return;
            hooked = true;

            textPrinter = Engine.GetService<ITextPrinterManager>();
            if (textPrinter != null) textPrinter.OnPrintStarted += OnPrintStarted;

            scriptPlayer = Engine.GetService<IScriptPlayer>();
            if (scriptPlayer != null)
            {
                scriptPlayer.OnCommandExecutionStart += OnCommandExecutionStart;
                scriptPlayer.OnPlay += OnPlay;
                scriptPlayer.OnStop += OnStop;
                scriptPlayer.OnSkip += OnSkip;
                scriptPlayer.OnAutoPlay += OnAutoPlay;
                scriptPlayer.OnWaitingForInput += OnWaitingForInput;
            }

            choiceHandler = Engine.GetService<IChoiceHandlerManager>();
            uiManager = Engine.GetService<IUIManager>();
        }

        public void Poll()
        {
            if (!hooked) return;

            // 服务可能在初始化后才出现，持续补取
            if (textPrinter == null) textPrinter = Engine.GetService<ITextPrinterManager>();
            if (scriptPlayer == null) scriptPlayer = Engine.GetService<IScriptPlayer>();
            if (choiceHandler == null) choiceHandler = Engine.GetService<IChoiceHandlerManager>();
            if (uiManager == null) uiManager = Engine.GetService<IUIManager>();

            TrySubscribeChoicePanel();
            TrySubscribeUIs();
            PollChoices();
            PollHiyakawa();
            FlushPendingAnnounces();
            if (clickAssist != null) clickAssist.Poll();
        }

        // ---------- 对话 ----------

        private static readonly System.Text.RegularExpressions.Regex RichTextRegex =
            new System.Text.RegularExpressions.Regex("<[^>]+>", System.Text.RegularExpressions.RegexOptions.Compiled);

        public static string CleanText(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            string s = RichTextRegex.Replace(raw, " ");
            return s.Replace("\r", " ").Replace("\n", " ").Replace("\\n", " ").Trim();
        }

        private void OnPrintStarted(PrintMessageArgs args)
        {
            try
            {
                string text = CleanText(args.Message.Text); // LocalizableText -> string（本地化已解析）
                if (string.IsNullOrWhiteSpace(text)) return;

                if (args.Append)
                {
                    lastDialogueText += text;
                }
                else
                {
                    lastDialogueText = text;
                }

                string author = null;
                if (args.Message.Author.HasValue)
                {
                    var a = args.Message.Author.Value;
                    string label = a.Label; // LocalizableText -> string
                    author = string.IsNullOrWhiteSpace(label) ? a.Id : label;
                }
                sender.SendDialogue(lastDialogueText, author);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("SopAccess: OnPrintStarted error: " + e.Message);
            }
        }

        // ---------- 命令执行（读心台词等） ----------

        private void OnCommandExecutionStart(Command command)
        {
            try
            {
                if (command is CustomCommands.MindReadingSideUI mind)
                {
                    string content = null;
                    string emotion = null;
                    try
                    {
                        if (Command.Assigned(mind.Content)) content = mind.Content;
                        if (Command.Assigned(mind.Emotion)) emotion = mind.Emotion;
                    }
                    catch { }
                    if (string.IsNullOrWhiteSpace(content)) content = mind.Content;
                    if (string.IsNullOrWhiteSpace(emotion)) emotion = mind.Emotion;
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        sender.SendThought(CleanText(content), emotion);
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("SopAccess: OnCommandExecutionStart error: " + e.Message);
            }
        }

        // ---------- 游戏状态 ----------

        private void OnPlay(Script script)
        {
            try
            {
                string name = script != null ? script.Path : null;
                sender.SendPlay(name);
            }
            catch (Exception e) { Plugin.Log.LogWarning("SopAccess: OnPlay error: " + e.Message); }
        }

        private void OnStop(Script script)
        {
            try { sender.SendStop(); }
            catch (Exception e) { Plugin.Log.LogWarning("SopAccess: OnStop error: " + e.Message); }
        }

        private void OnSkip(bool on)
        {
            try { sender.SendMode("skip", on); }
            catch (Exception e) { Plugin.Log.LogWarning("SopAccess: OnSkip error: " + e.Message); }
        }

        private void OnAutoPlay(bool on)
        {
            try { sender.SendMode("auto", on); }
            catch (Exception e) { Plugin.Log.LogWarning("SopAccess: OnAutoPlay error: " + e.Message); }
        }

        private void OnWaitingForInput(bool on)
        {
            try { sender.SendWaitingInput(on); }
            catch (Exception e) { Plugin.Log.LogWarning("SopAccess: OnWaitingForInput error: " + e.Message); }
        }

        // ---------- 选项 ----------

        private void TrySubscribeChoicePanel()
        {
            if (choicePanelSubscribed) return;
            if (uiManager == null) return;
            var panel = uiManager.GetUI<ChoiceHandlerPanel>();
            if (panel == null) return;
            choicePanel = panel;
            choicePanel.OnChoice += OnChoicePicked;
            choicePanelSubscribed = true;
        }

        private void OnChoicePicked(ChoiceState choice)
        {
            try
            {
                string summary = CleanText(choice.Summary); // LocalizableText -> string
                sender.SendPicked(string.IsNullOrWhiteSpace(summary) ? choice.Id : summary);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("SopAccess: OnChoicePicked error: " + e.Message);
            }
        }

        private void PollChoices()
        {
            if (choiceHandler == null) return;
            try
            {
                var opts = new List<string>();
                int actorCount = 0;
                foreach (var actor in choiceHandler.Actors)
                {
                    actorCount++;
                    if (!(actor is IChoiceHandlerActor cha)) continue;
                    foreach (var c in cha.Choices)
                    {
                        string s = CleanText(c.Summary);
                        if (string.IsNullOrWhiteSpace(s)) continue;
                        opts.Add(c.Locked ? "（未解锁）" + s : s);
                    }
                }
                if (actorCount > 0)
                {
                    Plugin.Log.LogInfo("SopAccess: choice actors=" + actorCount + " options=" + opts.Count);
                }

                bool same = opts.Count == lastOptions.Count;
                if (same)
                {
                    for (int i = 0; i < opts.Count; i++)
                    {
                        if (opts[i] != lastOptions[i]) { same = false; break; }
                    }
                }
                if (!same)
                {
                    lastOptions = opts;
                    if (opts.Count > 0)
                    {
                        sender.SendChoice(opts);
                        Plugin.Log.LogInfo("SopAccess: choices -> " + string.Join(" | ", opts.ToArray()));
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("SopAccess: PollChoices error: " + e.Message);
            }
        }

        // ---------- 通用界面面板 ----------

        private static string PanelId(IManagedUI ui)
        {
            string typeName = ui.GetType().Name;
            if (typeName == "CustomUI" && ui is Component c)
            {
                try { return c.gameObject.name; } catch { }
            }
            return typeName;
        }

        private void TrySubscribeUIs()
        {
            if (uiManager == null) return;
            try
            {
                var uis = new List<IManagedUI>();
                uiManager.GetManagedUIs(uis);
                foreach (var ui in uis)
                {
                    if (ui == null) continue;
                    if (subscribedUIs.Contains(ui)) continue;
                    // 捕获 ui 实例，避免事件签名歧义
                    Action<bool> handler = visible => OnVisibilityChanged(visible, ui);
                    ui.OnVisibilityChanged += handler;
                    subscribedUIs.Add(ui);
                    uiVisibilityHandlers[ui] = handler;

                    // 订阅时若已可见，稍后播报一次（例如标题画面）
                    if (ui.Visible)
                    {
                        HandleUIVisible(ui);
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("SopAccess: TrySubscribeUIs error: " + e.Message);
            }
        }

        private void OnVisibilityChanged(bool visible, IManagedUI ui)
        {
            try
            {
                string id = PanelId(ui);

                if (!visible)
                {
                    pendingAnnounces.Remove(id);
                    if (announcedPanels.Remove(id))
                    {
                        string extra = null;
                        if (ui is HiyakawaInputUI hiyakawa) extra = hiyakawaLastText;
                        sender.SendUIHide(id, extra);
                    }
                    return;
                }

                // 对话/选项/输入面板走各自的事件通道，避免重复
                HandleUIVisible(ui);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("SopAccess: OnVisibilityChanged error: " + e.Message);
            }
        }

        private void HandleUIVisible(IManagedUI ui)
        {
            try
            {
                if (ui is UITextPrinterPanel) return;
                if (ui is ChoiceHandlerPanel) return;
                if (ui is HiyakawaInputUI) return;
                if (NoisePanels.Contains(PanelId(ui))) return;

                ScheduleAnnounce(ui, 0.7f);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("SopAccess: HandleUIVisible error: " + e.Message);
            }
        }

        private void ScheduleAnnounce(IManagedUI ui, float delay)
        {
            string id = PanelId(ui);
            pendingAnnounces[id] = new PendingAnnounce { UI = ui, AtTime = Time.unscaledTime + delay };
        }

        private void FlushPendingAnnounces()
        {
            if (pendingAnnounces.Count == 0) return;
            float now = Time.unscaledTime;
            var ready = new List<string>();
            foreach (var kv in pendingAnnounces)
            {
                if (now >= kv.Value.AtTime) ready.Add(kv.Key);
            }
            foreach (string id in ready)
            {
                PendingAnnounce pa;
                if (!pendingAnnounces.TryGetValue(id, out pa)) continue;
                pendingAnnounces.Remove(id);

                IManagedUI ui = pa.UI;
                bool stillVisible;
                try { stillVisible = ui != null && ui.Visible && ((Component)ui).gameObject.activeInHierarchy; }
                catch { stillVisible = false; }
                if (!stillVisible) continue;

                var comp = ui as Component;
                if (comp == null) continue;

                string title;
                var buttons = new List<string>();
                PanelDescriber.Describe(comp.gameObject, out title, buttons);
                announcedPanels.Add(id);
                sender.SendUI(id, title, buttons);
            }
        }

        // ---------- 读心输入 UI ----------

        private void PollHiyakawa()
        {
            if (uiManager == null) return;
            if (hiyakawaUI == null)
            {
                hiyakawaUI = uiManager.GetUI<HiyakawaInputUI>();
                if (hiyakawaUI == null) return;
            }

            bool vis;
            try { vis = hiyakawaUI.Visible && hiyakawaUI.gameObject.activeInHierarchy; }
            catch { return; }

            if (vis != hiyakawaVisible)
            {
                hiyakawaVisible = vis;
                hiyakawaLastText = string.Empty;
                if (vis)
                {
                    string title;
                    var buttons = new List<string>();
                    PanelDescriber.Describe(hiyakawaUI.gameObject, out title, buttons);
                    sender.SendUI("HiyakawaInputUI", title, buttons);
                }
                else
                {
                    sender.SendInputSubmit(hiyakawaLastText);
                }
            }

            if (vis && Time.unscaledTime >= nextHiyakawaCheck)
            {
                nextHiyakawaCheck = Time.unscaledTime + 0.12f;
                try
                {
                    var input = hiyakawaUI.GetComponentInChildren<TMP_InputField>(true);
                    if (input != null && input.text != hiyakawaLastText)
                    {
                        hiyakawaLastText = input.text;
                        sender.SendInput(hiyakawaLastText);
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("SopAccess: PollHiyakawa error: " + e.Message);
                }
            }
        }

        // ---------- 卸载 ----------

        public void Unhook()
        {
            if (!hooked) return;
            hooked = false;
            try
            {
                if (textPrinter != null) textPrinter.OnPrintStarted -= OnPrintStarted;
                if (scriptPlayer != null)
                {
                    scriptPlayer.OnCommandExecutionStart -= OnCommandExecutionStart;
                    scriptPlayer.OnPlay -= OnPlay;
                    scriptPlayer.OnStop -= OnStop;
                    scriptPlayer.OnSkip -= OnSkip;
                    scriptPlayer.OnAutoPlay -= OnAutoPlay;
                    scriptPlayer.OnWaitingForInput -= OnWaitingForInput;
                }
                if (choicePanel != null && choicePanelSubscribed)
                {
                    choicePanel.OnChoice -= OnChoicePicked;
                    choicePanelSubscribed = false;
                }
                foreach (var kv in uiVisibilityHandlers)
                {
                    try { kv.Key.OnVisibilityChanged -= kv.Value; } catch { }
                }
                uiVisibilityHandlers.Clear();
                subscribedUIs.Clear();
                pendingAnnounces.Clear();
                announcedPanels.Clear();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("SopAccess: Unhook error: " + e.Message);
            }
        }
    }
}
