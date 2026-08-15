// ClickAssist — 可点击目标收集与点击辅助。
// 收集场景中的可点击对象（uGUI 按钮与 WorldScriptTriggerButton 世界热点），
// 播报其名称与屏幕方位；收到 NVDA 插件命令后移动光标并模拟点击。
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Naninovel;
using Naninovel.UI;
using nysec.UI;
using Projects.Background;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace SopAccess
{
    public sealed class ClickAssist
    {
        // 排除列表：仅对话打印机、输入层与装饰噪音层。
        // 所有菜单（主菜单/存档/设置/选项框/画廊）的按钮都进入可点击位置。
        private static readonly HashSet<string> ExcludedPanelTypes = new HashSet<string>
        {
            "UITextPrinterPanel", "RevealableTextPrinterPanel", "SimpleCustomRevealableTextPrinterPanel",
            "HiyakawaInputUI", "LoadingPanel", "ContinueInputUI", "ClickThroughPanel",
            "ToastUI", "ExternalScriptsBrowserPanel", "ScriptNavigatorPanel",
            "VariableInputPanel", "SceneTransitionUI", "CreditsVideoUI",
        };

        // CustomUI 类型面板的 GameObject 名噪音列表（装饰性/虚拟鼠标等，其按钮不可点）
        private static readonly HashSet<string> CustomUINoiseNames = new HashSet<string>
        {
            "VirtualMouseUI", "VirtualMouse", "ClickThrough", "ToastUI", "ContinueInput",
        };

        private readonly EventSender sender;
        private readonly List<ClickableItem> targets = new List<ClickableItem>();
        private string lastSignature = string.Empty;
        private float nextClickableCheck;
        private float nextFocusCheck;
        private string lastFocusName;
        private volatile int pendingClickIndex = -1;

        // 自测模式：存在标记文件 %TEMP%\sop_autoclick.flag 时自动逐个点击目标（验证点击命中）
        private static readonly string AutoClickFlag = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sop_autoclick.flag");
        private bool autoClickTest;
        private float nextAutoClickCheck;
        private int autoClickIndex;
        private float nextAutoClickTime;

        // 游戏原生点击状态机（虚拟手柄/虚拟鼠标注入 + 命中验证 + 自动重试）
        private enum ClickStage { Idle, Pressed, Restoring, Verifying, Retrying }
        private ClickStage clickStage = ClickStage.Idle;
        private float clickStageTime;
        private UnityEngine.InputSystem.Gamepad injectedPad;
        private string pendingClickLabel = string.Empty;
        private int attemptIndex;
        private readonly List<Vector2> attemptPoints = new List<Vector2>();
        private object verifyRef;
        private string verifyKind = string.Empty;

        // 点击验证（反射读取世界热点的 clicked 标志；UI 按钮用 onClick 钩子）
        private static System.Reflection.FieldInfo wstbClickedField;
        private static System.Reflection.FieldInfo clickTargetClickedField;
        private static System.Reflection.FieldInfo clickTargetIdField;
        private readonly Dictionary<Button, bool> uiClickFlags = new Dictionary<Button, bool>();
        private readonly HashSet<Button> hookedButtons = new HashSet<Button>();

        public ClickAssist(EventSender sender)
        {
            this.sender = sender;
            try
            {
                wstbClickedField = typeof(WorldScriptTriggerButton).GetField(
                    "clicked", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                clickTargetClickedField = typeof(ClickAdvanceTarget).GetField(
                    "clickedThisFrame", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                clickTargetIdField = typeof(ClickAdvanceTarget).GetField(
                    "id", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            }
            catch { }
        }

        public void Poll()
        {
            PollFocus();
            PollClickables();
            ConsumeClickCommand();
            PollClickStage();
            PollAutoClickFlag();
            if (autoClickTest) PollAutoClick();
        }

        private void PollAutoClickFlag()
        {
            if (Time.unscaledTime < nextAutoClickCheck) return;
            nextAutoClickCheck = Time.unscaledTime + 1.0f;
            bool flag = System.IO.File.Exists(AutoClickFlag);
            if (flag != autoClickTest)
            {
                autoClickTest = flag;
                autoClickIndex = 0;
                Plugin.Log.LogInfo("SopAccess: AUTOCLICK TEST MODE " + (flag ? "ENABLED" : "DISABLED"));
            }
        }

        private void PollAutoClick()
        {
            if (targets.Count == 0) return;
            if (Time.unscaledTime < nextAutoClickTime) return;
            nextAutoClickTime = Time.unscaledTime + 2.5f;
            if (autoClickIndex >= targets.Count) autoClickIndex = 0;
            Plugin.Log.LogInfo("SopAccess: AUTOCLICK target " + autoClickIndex + " (" + targets[autoClickIndex].Label + ")");
            pendingClickIndex = autoClickIndex;
            autoClickIndex++;
        }

        // ---------- 焦点播报（菜单/选项的方向键导航反馈） ----------

        private void PollFocus()
        {
            if (Time.unscaledTime < nextFocusCheck) return;
            nextFocusCheck = Time.unscaledTime + 0.1f;
            try
            {
                var es = UnityEngine.EventSystems.EventSystem.current;
                var go = es != null ? es.currentSelectedGameObject : null;
                string label = null;
                if (go != null && go.activeInHierarchy)
                {
                    label = FocusLabel(go);
                }
                if (label != lastFocusName)
                {
                    lastFocusName = label;
                    if (!string.IsNullOrEmpty(label))
                    {
                        sender.SendFocus(label);
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("SopAccess: PollFocus error: " + e.Message);
            }
        }

        private static string FocusLabel(GameObject go)
        {
            foreach (var t in go.GetComponentsInChildren<TMP_Text>(true))
            {
                if (t == null) continue;
                string s = t.text;
                if (string.IsNullOrWhiteSpace(s)) continue;
                s = GameHooks.CleanText(s);
                if (s.Length > 0) return s;
            }
            return go.name;
        }

        // ---------- 可点击目标收集 ----------

        // 门控：仅保留“场景按钮必然被遮挡/不可用”的面板。
        // 游戏内主菜单（控制面板）、存档菜单、设置等全部不门控——其按钮进入可点击位置。
        private static readonly HashSet<string> MenuPanelTypes = new HashSet<string>
        {
            "HiyakawaInputUI", "LoadingPanel",
        };

        private bool AnyMenuPanelVisible()
        {
            try
            {
                var uim = Engine.GetService<IUIManager>();
                if (uim == null) return false;
                var uis = new List<IManagedUI>();
                uim.GetManagedUIs(uis);
                foreach (var ui in uis)
                {
                    if (ui == null || !ui.Visible) continue;
                    if (MenuPanelTypes.Contains(ui.GetType().Name)) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>把对象投影到屏幕：优先 Naninovel 相机，若对象不在其视锥内则找包含它的相机。</summary>
        private static bool TryProjectToScreen(Transform target, Collider2D col, out Vector3 screenPos, out UnityEngine.Camera cam)
        {
            screenPos = Vector3.zero;
            cam = null;
            try
            {
                var candidates = new List<UnityEngine.Camera>();
                var nanoCam = Engine.GetService<ICameraManager>()?.Camera;
                if (nanoCam != null) candidates.Add(nanoCam);
                var mainCam = UnityEngine.Camera.main;
                if (mainCam != null && !candidates.Contains(mainCam)) candidates.Add(mainCam);
                foreach (var c in UnityEngine.Camera.allCameras)
                {
                    if (c != null && c.enabled && !candidates.Contains(c)) candidates.Add(c);
                }

                Bounds b = col != null ? col.bounds : new Bounds(target.position, Vector3.one);
                UnityEngine.Camera bestCam = null;
                float bestDist = float.MaxValue;
                foreach (var c in candidates)
                {
                    if (c == null || !c.enabled) continue;
                    try
                    {
                        var planes = GeometryUtility.CalculateFrustumPlanes(c);
                        if (GeometryUtility.TestPlanesAABB(planes, b))
                        {
                            Vector3 sp = c.WorldToScreenPoint(b.center);
                            if (sp.z < 0 || sp.z > c.farClipPlane) continue;
                            float d = Vector3.Distance(c.transform.position, b.center);
                            if (d < bestDist)
                            {
                                bestDist = d;
                                bestCam = c;
                                screenPos = sp;
                            }
                        }
                    }
                    catch { }
                }
                if (bestCam == null && nanoCam != null)
                {
                    Vector3 sp = nanoCam.WorldToScreenPoint(b.center);
                    if (sp.z >= 0)
                    {
                        bestCam = nanoCam;
                        screenPos = sp;
                    }
                }
                cam = bestCam;
                return bestCam != null;
            }
            catch { return false; }
        }

        private void PollClickables()
        {
            if (Time.unscaledTime < nextClickableCheck) return;
            nextClickableCheck = Time.unscaledTime + 1.0f; // 全场景扫描较重在弱机上有负担，1 秒一次
            try
            {
                // 菜单打开时场景按钮被遮挡，暂停扫描（菜单按钮由焦点播报覆盖）
                if (AnyMenuPanelVisible())
                {
                    if (targets.Count > 0)
                    {
                        targets.Clear();
                        lastSignature = string.Empty;
                        sender.SendClickable(new List<ClickableItem>());
                    }
                    return;
                }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var found = new List<ClickableItem>();
                var seen = new HashSet<GameObject>();

                foreach (var b in UnityEngine.Object.FindObjectsOfType<Button>())
                {
                    if (b == null || !b.interactable || !b.isActiveAndEnabled) continue;
                    var go = b.gameObject;
                    if (!go.activeInHierarchy || seen.Contains(go)) continue;
                    if (IsEffectivelyHidden(go.transform)) continue; // 隐藏/禁交互的 UI（如视频控制条）
                    if (!IsRendered(go)) continue; // 无实际渲染内容
                    if (IsUnderExcludedPanel(go.transform)) continue;
                    Vector2 center = ButtonScreenCenter(b);
                    if (center.x < -1 || center.y < -1) continue;
                    if (center.x < 0 || center.y < 0 || center.x > Screen.width || center.y > Screen.height) continue;
                    // 跳过几乎全屏的点击区（通常是“继续”透明层）
                    float w = Mathf.Abs(ButtonScreenSize(b).x);
                    float h = Mathf.Abs(ButtonScreenSize(b).y);
                    if (w > Screen.width * 0.7f && h > Screen.height * 0.7f) continue;
                    seen.Add(go);
                    EnsureClickHook(b);
                    // AlphaHitTestButton：点击需要落在图片不透明像素上，偏移到最近不透明点
                    Vector2 hit = AdjustClickPointForAlpha(b, center);
                    found.Add(new ClickableItem
                    {
                        Label = ClickableLabel(go),
                        X = center.x / Screen.width,
                        Y = center.y / Screen.height,
                        ClickX = hit.x / Screen.width,
                        ClickY = hit.y / Screen.height,
                        Kind = "ui",
                        Ref = b,
                    });
                }

                foreach (var w in UnityEngine.Object.FindObjectsOfType<WorldScriptTriggerButton>())
                {
                    if (w == null || !w.isActiveAndEnabled) continue;
                    var go = w.gameObject;
                    if (!go.activeInHierarchy || seen.Contains(go)) continue;
                    if (IsEffectivelyHidden(go.transform)) continue;
                    var col = w.GetComponentInChildren<Collider2D>(true);
                    if (col == null) continue;
                    Vector3 sp;
                    UnityEngine.Camera cam;
                    if (!TryProjectToScreen(go.transform, col, out sp, out cam)) continue;
                    if (sp.z < 0) continue;
                    if (sp.x < 0 || sp.y < 0 || sp.x > Screen.width || sp.y > Screen.height) continue;
                    seen.Add(go);
                    // 用 collider 上“确实在形状内/边界上”的点作为点击点（bounds 中心可能落在不规则形状外）
                    Vector2 clickPt = sp;
                    try
                    {
                        Vector2 worldCenter = cam.ScreenToWorldPoint(new Vector3(sp.x, sp.y, sp.z));
                        Vector2 closest = col.ClosestPoint(worldCenter);
                        Vector3 csp = cam.WorldToScreenPoint(new Vector3(closest.x, closest.y, sp.z));
                        if (csp.x >= 0 && csp.y >= 0 && csp.x <= Screen.width && csp.y <= Screen.height)
                        {
                            clickPt = new Vector2(csp.x, csp.y);
                        }
                    }
                    catch { }
                    found.Add(new ClickableItem
                    {
                        Label = ClickableLabel(go),
                        X = sp.x / Screen.width,
                        Y = sp.y / Screen.height,
                        ClickX = clickPt.x / Screen.width,
                        ClickY = clickPt.y / Screen.height,
                        Kind = "world",
                        Ref = w,
                    });
                }

                // @waitClick 世界点击目标（ClickAdvanceTarget：妹妹身体部位/货架物品/社团社员等）
                foreach (var ct in UnityEngine.Object.FindObjectsOfType<ClickAdvanceTarget>())
                {
                    if (ct == null || !ct.isActiveAndEnabled) continue;
                    var go = ct.gameObject;
                    if (!go.activeInHierarchy || seen.Contains(go)) continue;
                    if (IsEffectivelyHidden(go.transform)) continue;
                    var col = ct.GetComponentInChildren<Collider2D>(true);
                    if (col == null) continue;
                    Vector3 sp;
                    UnityEngine.Camera cam;
                    if (!TryProjectToScreen(go.transform, col, out sp, out cam)) continue;
                    if (sp.z < 0) continue;
                    if (sp.x < 0 || sp.y < 0 || sp.x > Screen.width || sp.y > Screen.height) continue;
                    seen.Add(go);
                    string label = go.name;
                    if (clickTargetIdField != null)
                    {
                        try
                        {
                            string id = (string)clickTargetIdField.GetValue(ct);
                            if (!string.IsNullOrWhiteSpace(id)) label = id;
                        }
                        catch { }
                    }
                    Vector2 clickPt = sp;
                    try
                    {
                        Vector2 worldCenter = cam.ScreenToWorldPoint(new Vector3(sp.x, sp.y, sp.z));
                        Vector2 closest = col.ClosestPoint(worldCenter);
                        Vector3 csp = cam.WorldToScreenPoint(new Vector3(closest.x, closest.y, sp.z));
                        if (csp.x >= 0 && csp.y >= 0 && csp.x <= Screen.width && csp.y <= Screen.height)
                        {
                            clickPt = new Vector2(csp.x, csp.y);
                        }
                    }
                    catch { }
                    found.Add(new ClickableItem
                    {
                        Label = label,
                        X = sp.x / Screen.width,
                        Y = sp.y / Screen.height,
                        ClickX = clickPt.x / Screen.width,
                        ClickY = clickPt.y / Screen.height,
                        Kind = "clickTarget",
                        Ref = ct,
                    });
                }

                // 星星彩蛋（StarClickHandler）
                foreach (var sh in UnityEngine.Object.FindObjectsOfType<StarClickHandler>())
                {
                    if (sh == null || !sh.isActiveAndEnabled) continue;
                    var go = sh.gameObject;
                    if (!go.activeInHierarchy || seen.Contains(go)) continue;
                    var col = go.GetComponentInChildren<Collider2D>(true);
                    if (col == null) continue;
                    Vector3 sp;
                    UnityEngine.Camera cam;
                    if (!TryProjectToScreen(go.transform, col, out sp, out cam)) continue;
                    if (sp.z < 0) continue;
                    if (sp.x < 0 || sp.y < 0 || sp.x > Screen.width || sp.y > Screen.height) continue;
                    seen.Add(go);
                    found.Add(new ClickableItem
                    {
                        Label = go.name,
                        X = sp.x / Screen.width,
                        Y = sp.y / Screen.height,
                        ClickX = sp.x / Screen.width,
                        ClickY = sp.y / Screen.height,
                        Kind = "world2",
                        Ref = sh,
                    });
                }

                // 按阅读顺序排序：上→下（屏幕 y 自下而上，故 y 大者在前），同排左→右
                found.Sort(delegate (ClickableItem a, ClickableItem b)
                {
                    int by = b.Y.CompareTo(a.Y);
                    if (by != 0) return by;
                    return a.X.CompareTo(b.X);
                });

                // 去重（同标签且位置接近）
                var dedup = new List<ClickableItem>();
                foreach (var it in found)
                {
                    bool dup = false;
                    foreach (var d in dedup)
                    {
                        if (d.Label == it.Label && Mathf.Abs(d.X - it.X) < 0.02f && Mathf.Abs(d.Y - it.Y) < 0.02f)
                        {
                            dup = true;
                            break;
                        }
                    }
                    if (!dup)
                    {
                        dedup.Add(it);
                        if (dedup.Count >= 20) break;
                    }
                }

                string sig = BuildSignature(dedup);
                if (sig != lastSignature)
                {
                    lastSignature = sig;
                    targets.Clear();
                    targets.AddRange(dedup);
                    autoClickIndex = 0;
                    var sb = new System.Text.StringBuilder("SopAccess: clickable set (");
                    sb.Append(dedup.Count).Append("):");
                    for (int i = 0; i < dedup.Count; i++)
                    {
                        sb.Append(" [").Append(i).Append("] ").Append(dedup[i].Label)
                          .Append(" @(").Append(dedup[i].X.ToString("0.00")).Append(',')
                          .Append(dedup[i].Y.ToString("0.00")).Append(')');
                    }
                    Plugin.Log.LogInfo(sb.ToString());
                    sender.SendClickable(dedup);
                }
                sw.Stop();
                if (sw.ElapsedMilliseconds > 100)
                {
                    Plugin.Log.LogInfo("SopAccess: clickable scan took " + sw.ElapsedMilliseconds + " ms, targets=" + dedup.Count);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("SopAccess: PollClickables error: " + e.Message);
            }
        }

        private static string BuildSignature(List<ClickableItem> items)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var it in items)
            {
                sb.Append(it.Label).Append('@')
                  .Append(Mathf.RoundToInt(it.X * 10)).Append(',')
                  .Append(Mathf.RoundToInt(it.Y * 10)).Append(';');
            }
            return sb.ToString();
        }

        private static bool IsUnderExcludedPanel(Transform t)
        {
            while (t != null)
            {
                foreach (var c in t.GetComponents<Component>())
                {
                    if (c == null) continue;
                    string n = c.GetType().Name;
                    if (n == "CustomUI")
                    {
                        // CustomUI 类型：按 GameObject 名过滤噪音（场景面板如 SmartPhone_CG_UI 需保留）
                        if (CustomUINoiseNames.Contains(t.gameObject.name)) return true;
                        continue;
                    }
                    if (ExcludedPanelTypes.Contains(n)) return true;
                }
                t = t.parent;
            }
            return false;
        }

        /// <summary>检查对象及其父链是否被 CanvasGroup/Canvas 隐藏或禁用了交互。</summary>
        private static bool IsEffectivelyHidden(Transform t)
        {
            while (t != null)
            {
                var cg = t.GetComponent<CanvasGroup>();
                if (cg != null)
                {
                    if (cg.alpha <= 0.02f) return true;
                    if (!cg.interactable) return true;
                    if (!cg.blocksRaycasts) return true;
                }
                var cv = t.GetComponent<Canvas>();
                if (cv != null && !cv.enabled) return true;
                t = t.parent;
            }
            return false;
        }

        /// <summary>按钮自身及子元素是否有任何实际渲染的内容（CanvasRenderer 未被剔除）。
        /// 注意：不检查 Graphic 透明度——uGUI 中透明图片依然可点击（空槽位等）。</summary>
        private static bool IsRendered(GameObject go)
        {
            foreach (var r in go.GetComponentsInChildren<CanvasRenderer>(true))
            {
                if (r == null || !r.gameObject.activeInHierarchy) continue;
                if (r.cull) continue;
                return true;
            }
            return false;
        }

        private static string ClickableLabel(GameObject go)
        {
            foreach (var t in go.GetComponentsInChildren<TMP_Text>(true))
            {
                if (t == null) continue;
                string s = t.text;
                if (string.IsNullOrWhiteSpace(s)) continue;
                s = GameHooks.CleanText(s);
                if (s.Length > 0) return s;
            }
            return go.name;
        }

        private static Vector2 ButtonScreenCenter(Button b)
        {
            var rt = b.GetComponent<RectTransform>();
            if (rt == null) return new Vector2(-1, -1);
            Vector3[] corners = new Vector3[4];
            rt.GetWorldCorners(corners);
            Vector3 center = (corners[0] + corners[2]) * 0.5f;
            var canvas = rt.GetComponentInParent<Canvas>();
            if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay && canvas.worldCamera != null)
            {
                center = canvas.worldCamera.WorldToScreenPoint(center);
            }
            return new Vector2(center.x, center.y);
        }

        /// <summary>
        /// AlphaHitTestButton（Image.alphaHitTestMinimumThreshold &gt; 0）要求点击落在图片
        /// 不透明像素上。从按钮矩形中心向外环形搜索最近的满足阈值的像素点并返回其屏幕坐标。
        /// </summary>
        private static Vector2 AdjustClickPointForAlpha(Button b, Vector2 screenCenter)
        {
            try
            {
                var img = b.GetComponent<Image>();
                if (img == null || img.sprite == null || img.sprite.texture == null) return screenCenter;
                if (img.alphaHitTestMinimumThreshold <= 0.001f) return screenCenter;

                var rt = b.GetComponent<RectTransform>();
                var canvas = rt.GetComponentInParent<Canvas>();
                Camera cam = (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay) ? canvas.worldCamera : null;

                Vector2 local;
                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, screenCenter, cam, out local))
                {
                    return screenCenter;
                }

                var tex = img.sprite.texture;
                Rect r = rt.rect;
                // 贴图采样范围（考虑 sprite 的 textureRect 偏移）
                Rect tr = img.sprite.textureRect;
                float texX0 = tr.xMin, texY0 = tr.yMin, texW = tr.width, texH = tr.height;
                if (texW <= 0 || texH <= 0) { texX0 = 0; texY0 = 0; texW = tex.width; texH = tex.height; }

                float thresh = img.alphaHitTestMinimumThreshold;

                Vector2 LocalToScreen(Vector2 lp)
                {
                    var corners = new Vector3[4];
                    rt.GetWorldCorners(corners);
                    // 线性映射：局部矩形 -> 世界矩形
                    float sx = Mathf.Lerp(corners[0].x, corners[2].x, (lp.x - r.xMin) / Mathf.Max(r.width, 0.0001f));
                    float sy = Mathf.Lerp(corners[0].y, corners[2].y, (lp.y - r.yMin) / Mathf.Max(r.height, 0.0001f));
                    if (cam != null)
                    {
                        var v3 = cam.WorldToScreenPoint(new Vector3(sx, sy, 0));
                        return new Vector2(v3.x, v3.y);
                    }
                    return new Vector2(sx, sy);
                }

                float AlphaAt(Vector2 lp)
                {
                    float u = (lp.x - r.xMin) / Mathf.Max(r.width, 0.0001f);
                    float v = (lp.y - r.yMin) / Mathf.Max(r.height, 0.0001f);
                    int px = Mathf.Clamp((int)(texX0 + u * texW), 0, tex.width - 1);
                    int py = Mathf.Clamp((int)(texY0 + v * texH), 0, tex.height - 1);
                    return tex.GetPixel(px, py).a;
                }

                // 环形搜索：从中心向外（步长 = 矩形宽高的 1/10；激进度越高搜索越广）
                int rings;
                float stepDiv;
                switch (clickOffsetLevel)
                {
                    case 0: return screenCenter; // 关闭偏移
                    case 1: rings = 2; stepDiv = 10f; break;
                    case 3: rings = 10; stepDiv = 16f; break;
                    default: rings = 5; stepDiv = 10f; break;
                }
                for (int ring = 0; ring <= rings; ring++)
                {
                    for (int gy = -ring; gy <= ring; gy++)
                    {
                        for (int gx = -ring; gx <= ring; gx++)
                        {
                            if (Mathf.Max(Mathf.Abs(gx), Mathf.Abs(gy)) != ring) continue;
                            var lp = new Vector2(
                                local.x + gx * r.width / stepDiv,
                                local.y + gy * r.height / stepDiv);
                            if (AlphaAt(lp) >= thresh)
                            {
                                return LocalToScreen(lp);
                            }
                        }
                    }
                }
                // 找不到不透明像素：退回矩形四角附近（激进度越高内缩越小，更靠近边缘）
                float inset = clickOffsetLevel >= 3 ? 0.15f : 0.3f;
                foreach (var corner in new[]
                {
                    new Vector2(r.xMin + r.width * inset, r.yMin + r.height * inset),
                    new Vector2(r.xMax - r.width * inset, r.yMin + r.height * inset),
                    new Vector2(r.xMin + r.width * inset, r.yMax - r.height * inset),
                    new Vector2(r.xMax - r.width * inset, r.yMax - r.height * inset),
                })
                {
                    if (AlphaAt(corner) >= thresh) return LocalToScreen(corner);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("SopAccess: AdjustClickPointForAlpha error: " + e.Message);
            }
            return screenCenter;
        }

        private static Vector2 ButtonScreenSize(Button b)
        {
            var rt = b.GetComponent<RectTransform>();
            if (rt == null) return Vector2.zero;
            Vector3[] corners = new Vector3[4];
            rt.GetWorldCorners(corners);
            return new Vector2(corners[2].x - corners[0].x, corners[2].y - corners[0].y);
        }

        // ---------- 点击执行 ----------

        public void HandleCommand(string type, int index)
        {
            if (type == "clickAt")
            {
                pendingClickIndex = index;
            }
            else if (type == "config")
            {
                clickOffsetLevel = Mathf.Clamp(index, 0, 3);
                Plugin.Log.LogInfo("SopAccess: clickOffsetLevel -> " + clickOffsetLevel);
            }
        }

        // 点空偏移激进度（0=关闭 1=温和 2=标准 3=激进），由 NVDA 插件配置下发
        private static int clickOffsetLevel = 2;

        /// <summary>读取 ClickAdvanceTarget 的 id（供诊断转储使用）。</summary>
        public string GetClickTargetId(ClickAdvanceTarget ct)
        {
            if (ct == null || clickTargetIdField == null) return null;
            try { return (string)clickTargetIdField.GetValue(ct); }
            catch { return null; }
        }

        private void ConsumeClickCommand()
        {
            int idx = pendingClickIndex;
            if (idx < 0) return;
            pendingClickIndex = -1;

            // 上一个点击尚未结算：立即结算（避免连续点击时结果丢失）
            if (clickStage != ClickStage.Idle)
            {
                try
                {
                    if (verifyKind == "ui" && cachedVmi != null && vmiVirtualMouseField != null)
                    {
                        var vm = vmiVirtualMouseField.GetValue(cachedVmi) as UnityEngine.InputSystem.Mouse;
                        if (vm != null) SetMouseButton(vm, UnityEngine.InputSystem.LowLevel.MouseButton.Left, false);
                    }
                    else
                    {
                        if (injectedPad != null)
                        {
                            try
                            {
                                UnityEngine.InputSystem.LowLevel.GamepadState padState;
                                injectedPad.CopyState<UnityEngine.InputSystem.LowLevel.GamepadState>(out padState);
                                padState.WithButton(UnityEngine.InputSystem.LowLevel.GamepadButton.South, false);
                                UnityEngine.InputSystem.LowLevel.InputState.Change(injectedPad, padState);
                            }
                            catch { }
                        }
                        InjectMouseRelease();
                    }
                }
                catch { }
                sender.SendClickResult(false, pendingClickLabel, attemptIndex + 1);
                clickStage = ClickStage.Idle;
            }

            try
            {
                if (idx < 0 || idx >= targets.Count)
                {
                    Plugin.Log.LogWarning("SopAccess: click target index out of range: " + idx);
                    return;
                }
                var tgt = targets[idx];
                pendingClickLabel = tgt.Label;
                verifyRef = tgt.Ref;
                verifyKind = tgt.Kind ?? string.Empty;

                // 生成点击尝试点序列：主点 + 四周偏移（应对不规则命中区域）
                attemptPoints.Clear();
                float px = tgt.ClickX * Screen.width;
                float py = tgt.ClickY * Screen.height;
                attemptPoints.Add(new Vector2(px, py));
                float ox = Mathf.Max(24f, Screen.width * 0.02f);
                float oy = Mathf.Max(24f, Screen.height * 0.02f);
                attemptPoints.Add(new Vector2(px - ox, py));
                attemptPoints.Add(new Vector2(px + ox, py));
                attemptPoints.Add(new Vector2(px, py - oy));
                attemptPoints.Add(new Vector2(px, py + oy));
                attemptIndex = 0;

                Plugin.Log.LogInfo("SopAccess: clicking target " + idx + " (" + tgt.Label + ", kind=" + verifyKind
                    + ") at " + px + "," + py);
                StartAttempt();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("SopAccess: click error: " + e.Message);
            }
        }

        /// <summary>对 UI 按钮挂钩 onClick 以验证点击命中（不影响游戏逻辑）。</summary>
        private void EnsureClickHook(Button b)
        {
            if (b == null || hookedButtons.Contains(b)) return;
            try
            {
                b.onClick.AddListener(() =>
                {
                    try { uiClickFlags[b] = true; } catch { }
                });
                hookedButtons.Add(b);
            }
            catch { }
        }

        private void PruneClickHooks()
        {
            if (hookedButtons.Count < 64) return;
            var dead = new List<Button>();
            foreach (var b in hookedButtons)
            {
                if (b == null) dead.Add(b);
            }
            foreach (var b in dead)
            {
                hookedButtons.Remove(b);
                uiClickFlags.Remove(b);
            }
        }

        /// <summary>检查当前目标是否已被点击命中。</summary>
        private bool CheckClickVerified()
        {
            try
            {
                if (verifyKind == "world" && wstbClickedField != null)
                {
                    var w = verifyRef as WorldScriptTriggerButton;
                    if (w == null) return true; // 目标已销毁：场景已变化，视为已处理
                    return (bool)wstbClickedField.GetValue(w);
                }
                if (verifyKind == "clickTarget" && clickTargetClickedField != null)
                {
                    var ct = verifyRef as ClickAdvanceTarget;
                    if (ct == null) return true;
                    return (bool)clickTargetClickedField.GetValue(ct);
                }
                if (verifyKind == "ui")
                {
                    var b = verifyRef as Button;
                    if (b == null) return true;
                    bool flag;
                    return uiClickFlags.TryGetValue(b, out flag) && flag;
                }
            }
            catch { }
            return true; // 未知类型：无法验证，不重试（游戏自身会有反馈）
        }

        private void StartAttempt()
        {
            if (attemptIndex >= attemptPoints.Count)
            {
                Plugin.Log.LogInfo("SopAccess: verify " + pendingClickLabel + " -> FAILED after " + attemptPoints.Count + " attempts");
                sender.SendClickResult(false, pendingClickLabel, attemptPoints.Count);
                clickStage = ClickStage.Idle;
                return;
            }
            var pt = attemptPoints[attemptIndex];
            Plugin.Log.LogInfo("SopAccess: attempt " + attemptIndex + " at " + pt.x + "," + pt.y);
            TryGameNativeClick(pt.x, pt.y);
        }

        /// <summary>通过游戏自身输入管道点击：
        /// - UI 按钮：直接驱动 VirtualMouseInput 的虚拟鼠标（位置 + leftButton），不切换输入模式；
        /// - 世界热点：切手柄模式 + WarpCursor 后立即按虚拟手柄 A（必须在鼠标抑制窗口内完成）。
        /// </summary>
        private void TryGameNativeClick(float px, float py)
        {
            try
            {
                if (verifyKind == "ui")
                {
                    if (ClickVirtualMouse(px, py)) return;
                    NativeInput.LeftClickAtClient(px, py);
                    clickStage = ClickStage.Verifying;
                    clickStageTime = Time.unscaledTime;
                    return;
                }

                var input = Engine.GetService<IInputManager>();
                if (input == null) { NativeInput.LeftClickAtClient(px, py); clickStage = ClickStage.Verifying; clickStageTime = Time.unscaledTime; return; }
                var vmc = UnityEngine.Object.FindObjectOfType<VirtualMouseController>();
                if (vmc == null) { NativeInput.LeftClickAtClient(px, py); clickStage = ClickStage.Verifying; clickStageTime = Time.unscaledTime; return; }

                // 切换到手柄模式：虚拟鼠标激活，世界热点由虚拟鼠标位置模拟
                input.InputMode = Naninovel.InputMode.Gamepad;
                vmc.WarpCursor(new Vector2(px, py));

                // 鼠标状态注入（覆盖 @waitClick 的 Mouse.current 检测）——必须先于手柄注入执行
                InjectMousePress(px, py);

                if (injectedPad == null)
                {
                    try
                    {
                        injectedPad = UnityEngine.InputSystem.InputSystem.AddDevice<UnityEngine.InputSystem.Gamepad>();
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning("SopAccess: AddDevice<Gamepad> error: " + e.Message);
                    }
                }
                if (injectedPad != null)
                {
                    // 立即按下手柄 A（bitfield 需整状态注入；失败也不影响鼠标注入路径）
                    clickStage = ClickStage.Pressed;
                    clickStageTime = Time.unscaledTime;
                    try
                    {
                        UnityEngine.InputSystem.LowLevel.GamepadState padState;
                        injectedPad.CopyState<UnityEngine.InputSystem.LowLevel.GamepadState>(out padState);
                        padState.WithButton(UnityEngine.InputSystem.LowLevel.GamepadButton.South, true);
                        UnityEngine.InputSystem.LowLevel.InputState.Change(injectedPad, padState);
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning("SopAccess: gamepad button inject failed (" + e.Message + "), mouse path still active");
                    }
                    return;
                }
                NativeInput.LeftClickAtClient(px, py);
                clickStage = ClickStage.Verifying;
                clickStageTime = Time.unscaledTime;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("SopAccess: game-native click failed (" + e.Message + "), fallback to physical click");
                NativeInput.LeftClickAtClient(px, py);
                clickStage = ClickStage.Verifying;
                clickStageTime = Time.unscaledTime;
            }
        }

        // VirtualMouseInput.m_VirtualMouse 反射缓存
        private static System.Reflection.FieldInfo vmiVirtualMouseField;
        private UnityEngine.InputSystem.UI.VirtualMouseInput cachedVmi;
        private UnityEngine.InputSystem.Mouse systemMouse;

        /// <summary>设置鼠标设备按钮状态（bitfield 必须整状态注入，与 VirtualMouseInput 内部一致）。</summary>
        private static void SetMouseButton(UnityEngine.InputSystem.Mouse mouse,
            UnityEngine.InputSystem.LowLevel.MouseButton button, bool pressed)
        {
            UnityEngine.InputSystem.LowLevel.MouseState state;
            mouse.CopyState<UnityEngine.InputSystem.LowLevel.MouseState>(out state);
            state.WithButton(button, pressed);
            UnityEngine.InputSystem.LowLevel.InputState.Change(mouse, state);
        }

        /// <summary>注入物理鼠标设备状态（位置+左键按下），覆盖 @waitClick 的 Mouse.current 检测。</summary>
        private void InjectMousePress(float px, float py)
        {
            try
            {
                if (systemMouse == null)
                {
                    systemMouse = UnityEngine.InputSystem.Mouse.current;
                }
                if (systemMouse == null) return;
                UnityEngine.InputSystem.LowLevel.InputState.Change(systemMouse.position, new Vector2(px, py));
                UnityEngine.InputSystem.LowLevel.InputState.Change(systemMouse.delta, Vector2.zero);
                SetMouseButton(systemMouse, UnityEngine.InputSystem.LowLevel.MouseButton.Left, true);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("SopAccess: InjectMousePress error: " + e.Message);
            }
        }

        private void InjectMouseRelease()
        {
            try
            {
                if (systemMouse != null)
                {
                    SetMouseButton(systemMouse, UnityEngine.InputSystem.LowLevel.MouseButton.Left, false);
                }
            }
            catch { }
        }

        /// <summary>直接驱动 VirtualMouseInput 的虚拟鼠标：移动位置并按下/释放左键。</summary>
        private bool ClickVirtualMouse(float px, float py)
        {
            try
            {
                if (cachedVmi == null)
                {
                    cachedVmi = UnityEngine.Object.FindObjectOfType<UnityEngine.InputSystem.UI.VirtualMouseInput>();
                }
                if (cachedVmi == null) return false;
                if (vmiVirtualMouseField == null)
                {
                    vmiVirtualMouseField = typeof(UnityEngine.InputSystem.UI.VirtualMouseInput).GetField(
                        "m_VirtualMouse", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                }
                if (vmiVirtualMouseField == null) return false;
                var vm = vmiVirtualMouseField.GetValue(cachedVmi) as UnityEngine.InputSystem.Mouse;
                if (vm == null) return false;

                // 移动虚拟鼠标到目标
                var vmc = UnityEngine.Object.FindObjectOfType<VirtualMouseController>();
                if (vmc != null) vmc.WarpCursor(new Vector2(px, py));
                UnityEngine.InputSystem.LowLevel.InputState.Change(vm.position, new Vector2(px, py));

                clickStage = ClickStage.Pressed;
                clickStageTime = Time.unscaledTime;
                SetMouseButton(vm, UnityEngine.InputSystem.LowLevel.MouseButton.Left, true);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("SopAccess: ClickVirtualMouse error: " + e.Message);
                return false;
            }
        }

        private void PollClickStage()
        {
            if (clickStage == ClickStage.Idle) return;
            float now = Time.unscaledTime;
            switch (clickStage)
            {
                case ClickStage.Pressed:
                    if (now - clickStageTime < 0.12f) return;
                    if (verifyKind == "ui")
                    {
                        // 释放虚拟鼠标左键
                        try
                        {
                            if (cachedVmi != null && vmiVirtualMouseField != null)
                            {
                                var vm = vmiVirtualMouseField.GetValue(cachedVmi) as UnityEngine.InputSystem.Mouse;
                                if (vm != null) SetMouseButton(vm, UnityEngine.InputSystem.LowLevel.MouseButton.Left, false);
                            }
                        }
                        catch { }
                    }
                    else
                    {
                        if (injectedPad != null)
                        {
                            try
                            {
                                UnityEngine.InputSystem.LowLevel.GamepadState padState;
                                injectedPad.CopyState<UnityEngine.InputSystem.LowLevel.GamepadState>(out padState);
                                padState.WithButton(UnityEngine.InputSystem.LowLevel.GamepadButton.South, false);
                                UnityEngine.InputSystem.LowLevel.InputState.Change(injectedPad, padState);
                            }
                            catch { }
                        }
                        InjectMouseRelease();
                    }
                    clickStage = ClickStage.Restoring;
                    clickStageTime = now;
                    break;

                case ClickStage.Restoring:
                    if (now - clickStageTime < 0.2f) return;
                    if (verifyKind != "ui")
                    {
                        try
                        {
                            var input = Engine.GetService<IInputManager>();
                            if (input != null) input.InputMode = Naninovel.InputMode.MouseAndKeyboard;
                        }
                        catch { }
                    }
                    clickStage = ClickStage.Verifying;
                    clickStageTime = now;
                    break;

                case ClickStage.Verifying:
                    if (now - clickStageTime < 0.25f) return;
                    if (CheckClickVerified())
                    {
                        Plugin.Log.LogInfo("SopAccess: verify " + pendingClickLabel + " -> clicked=True (attempt " + attemptIndex + ")");
                        sender.SendClickResult(true, pendingClickLabel, attemptIndex + 1);
                        clickStage = ClickStage.Idle;
                    }
                    else
                    {
                        clickStage = ClickStage.Retrying;
                        clickStageTime = now;
                    }
                    break;

                case ClickStage.Retrying:
                    if (now - clickStageTime < 0.4f) return;
                    attemptIndex++;
                    clickStage = ClickStage.Idle;
                    StartAttempt();
                    break;
            }
        }
    }

    /// <summary>系统级鼠标输入（P/Invoke）。</summary>
    internal static class NativeInput
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint Type;
            public MOUSEINPUT Mouse;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int Dx;
            public int Dy;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        private const uint INPUT_MOUSE = 0;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        /// <summary>把游戏客户区坐标转换为屏幕坐标并左键点击。</summary>
        public static void LeftClickAtClient(float clientX, float clientY)
        {
            int sx = (int)clientX;
            int sy = (int)clientY;
            try
            {
                // 窗口偏移（非全屏窗口时客户区原点≠屏幕原点）
                var hwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                if (hwnd != IntPtr.Zero)
                {
                    var origin = new POINT();
                    if (ClientToScreen(hwnd, ref origin))
                    {
                        sx = origin.X + (int)clientX;
                        sy = origin.Y + (int)clientY;
                    }
                }
            }
            catch { }

            SetCursorPos(sx, sy);
            System.Threading.Thread.Sleep(30);

            var down = new INPUT { Type = INPUT_MOUSE, Mouse = new MOUSEINPUT { Flags = MOUSEEVENTF_LEFTDOWN } };
            var up = new INPUT { Type = INPUT_MOUSE, Mouse = new MOUSEINPUT { Flags = MOUSEEVENTF_LEFTUP } };
            SendInput(1, new[] { down }, Marshal.SizeOf(typeof(INPUT)));
            System.Threading.Thread.Sleep(40);
            SendInput(1, new[] { up }, Marshal.SizeOf(typeof(INPUT)));
        }
    }
}
