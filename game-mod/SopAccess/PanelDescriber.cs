// PanelDescriber — 扫描可见 UI 面板，提取标题文本与按钮标签。
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SopAccess
{
    public static class PanelDescriber
    {
        private static readonly Regex TagRegex = new Regex("<[^>]+>", RegexOptions.Compiled);
        private static readonly Regex SpaceRegex = new Regex(@"\s+", RegexOptions.Compiled);

        public static void Describe(GameObject panel, out string title, List<string> buttons)
        {
            title = null;
            buttons.Clear();
            try
            {
                // 按钮标签：优先 Button，其次 Selectable / IPointerClickHandler（自定义按钮）
                var buttonsSeen = new HashSet<Component>();
                foreach (var clickable in panel.GetComponentsInChildren<Component>(true))
                {
                    if (clickable == null) continue;
                    if (!(clickable is Button) && !(clickable is Selectable) && !(clickable is UnityEngine.EventSystems.IPointerClickHandler)) continue;
                    if (clickable is TMP_Text || clickable is TMP_InputField || clickable is Image) continue;
                    var behaviour = clickable as Behaviour;
                    if (behaviour != null && (!behaviour.isActiveAndEnabled || !behaviour.gameObject.activeInHierarchy)) continue;
                    if (buttonsSeen.Contains(clickable)) continue;
                    buttonsSeen.Add(clickable);
                    // 只取最顶层可点击对象，避免重复
                    bool hasClickableParent = false;
                    var parent = clickable.transform.parent;
                    while (parent != null)
                    {
                        if (parent.GetComponent<Button>() != null || parent.GetComponent<Selectable>() != null || parent.GetComponent<UnityEngine.EventSystems.IPointerClickHandler>() != null)
                        {
                            hasClickableParent = true;
                            break;
                        }
                        parent = parent.parent;
                    }
                    if (hasClickableParent) continue;
                    string label = FindButtonLabel(clickable.gameObject);
                    if (string.IsNullOrWhiteSpace(label))
                    {
                        // 无文本标签（如悬停才显示文字的按钮），退而使用对象名
                        label = clickable.gameObject.name;
                    }
                    label = Normalize(label);
                    if (label.Length > 40) label = label.Substring(0, 40);
                    buttons.Add(label);
                    if (buttons.Count >= 24) break;
                }

                // 标题：面板中字号最大且不属于按钮/输入框的文本
                TMP_Text best = null;
                float bestSize = 0f;
                foreach (var text in panel.GetComponentsInChildren<TMP_Text>(true))
                {
                    if (!text.isActiveAndEnabled || !text.gameObject.activeInHierarchy) continue;
                    if (IsInsideInputField(text)) continue;
                    if (IsInsideButton(text)) continue;
                    float size = text.fontSize;
                    if (size > bestSize)
                    {
                        bestSize = size;
                        best = text;
                    }
                }
                if (best != null)
                {
                    string t = Normalize(best.text);
                    if (t.Length > 120) t = t.Substring(0, 120);
                    title = t;
                }
                if (string.IsNullOrWhiteSpace(title)) title = panel.name;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("SopAccess: PanelDescriber error: " + e.Message);
                if (string.IsNullOrWhiteSpace(title)) title = panel.name;
            }
        }

        private static string FindButtonLabel(GameObject buttonGo)
        {
            foreach (var text in buttonGo.GetComponentsInChildren<TMP_Text>(true))
            {
                if (!text.isActiveAndEnabled || !text.gameObject.activeInHierarchy) continue;
                string t = text.text;
                if (!string.IsNullOrWhiteSpace(t)) return t;
            }
            return null;
        }

        private static bool IsInsideInputField(Component comp)
        {
            return comp.GetComponentInParent<TMP_InputField>() != null;
        }

        private static bool IsInsideButton(Component comp)
        {
            return comp.GetComponentInParent<Button>() != null;
        }

        private static string Normalize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            string s = TagRegex.Replace(raw, " ");
            s = s.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");
            s = SpaceRegex.Replace(s, " ").Trim();
            return s;
        }
    }
}
