// EventSender — 通过本机 TCP (127.0.0.1:42137) 向 NVDA 插件发送换行分隔的 JSON 事件，
// 并接收来自插件的命令（如点击目标）。
using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SopAccess
{
    /// <summary>可点击目标（用于点击辅助）。</summary>
    public sealed class ClickableItem
    {
        public string Label;
        public float X; // 归一化 0..1（屏幕坐标，y 自下而上）
        public float Y;
        public float ClickX; // 实际点击位置（已按 Alpha 命中测试偏移）
        public float ClickY;
        public string Kind; // "ui" / "world"
        public object Ref; // Button / WorldScriptTriggerButton 实例（仅插件内部使用）
    }

    public sealed class EventSender : IDisposable
    {
        public const int DefaultPort = 42137;

        /// <summary>收到插件命令时触发（连接线程上调用，参数：类型、索引）。</summary>
        public event Action<string, int> OnCommand;

        private readonly object writeLock = new object();
        private TcpClient client;
        private NetworkStream stream;
        private Thread connThread;
        private volatile bool running = true;
        private string pendingInput = string.Empty;

        public EventSender()
        {
            connThread = new Thread(ConnectionLoop)
            {
                IsBackground = true,
                Name = "SopAccessConn"
            };
            connThread.Start();
        }

        private void ConnectionLoop()
        {
            while (running)
            {
                try
                {
                    using (var c = new TcpClient())
                    {
                        c.Connect("127.0.0.1", DefaultPort);
                        lock (writeLock)
                        {
                            client = c;
                            stream = c.GetStream();
                        }
                        Send("hello", new[] { ("v", "2"), ("plugin", "1.1.0") });
                        Plugin.Log.LogInfo("SopAccess: connected to NVDA addon listener.");
                        // 读循环：用 Poll 等待数据（200ms 周期），不设接收超时，
                        // 空闲超时≠断连；连接线程不持有写锁，避免主线程发送阻塞。
                        var buf = new byte[8192];
                        while (running)
                        {
                            bool readable;
                            try
                            {
                                readable = c.Client.Poll(200000, SelectMode.SelectRead);
                            }
                            catch (Exception)
                            {
                                break; // 套接字已失效
                            }
                            if (readable)
                            {
                                if (c.Client.Available == 0)
                                {
                                    break; // 对端关闭
                                }
                                int n = stream.Read(buf, 0, buf.Length);
                                if (n <= 0) break;
                                pendingInput += Encoding.UTF8.GetString(buf, 0, n);
                                int nl;
                                while ((nl = pendingInput.IndexOf('\n')) >= 0)
                                {
                                    string line = pendingInput.Substring(0, nl).Trim();
                                    pendingInput = pendingInput.Substring(nl + 1);
                                    if (line.Length > 0) HandleLine(line);
                                }
                            }
                            else if (!c.Connected)
                            {
                                break;
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("SopAccess: connection failed (" + e.Message + "), retrying...");
                }
                finally
                {
                    lock (writeLock)
                    {
                        if (stream != null) { try { stream.Dispose(); } catch { } stream = null; }
                        if (client != null) { try { client.Close(); } catch { } client = null; }
                    }
                }
                if (running) Thread.Sleep(2000);
            }
        }

        private void HandleLine(string line)
        {
            try
            {
                // 极简 JSON 解析（插件发来的命令很简单）
                string t = GetJsonField(line, "t");
                if (t == "clickAt")
                {
                    string idx = GetJsonField(line, "index");
                    int i;
                    if (int.TryParse(idx, out i))
                    {
                        var h = OnCommand;
                        if (h != null) h("clickAt", i);
                    }
                }
                else if (t == "dumpScript")
                {
                    var h = OnCommand;
                    if (h != null) h("dumpScript", 0);
                }
                else if (t == "dumpScene")
                {
                    var h = OnCommand;
                    if (h != null) h("dumpScene", 0);
                }
                else if (t == "config")
                {
                    string v = GetJsonField(line, "clickOffset");
                    int lvl;
                    if (int.TryParse(v, out lvl))
                    {
                        var h = OnCommand;
                        if (h != null) h("config", lvl);
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("SopAccess: handle command error: " + e.Message);
            }
        }

        private static string GetJsonField(string json, string field)
        {
            string key = "\"" + field + "\":";
            int i = json.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return null;
            i += key.Length;
            while (i < json.Length && (json[i] == ' ' || json[i] == '\t')) i++;
            if (i < json.Length && json[i] == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < json.Length && json[i] != '"')
                {
                    if (json[i] == '\\' && i + 1 < json.Length) { i++; }
                    sb.Append(json[i]);
                    i++;
                }
                return sb.ToString();
            }
            var num = new StringBuilder();
            while (i < json.Length && (char.IsDigit(json[i]) || json[i] == '-' || json[i] == '.'))
            {
                num.Append(json[i]);
                i++;
            }
            return num.ToString();
        }

        public void SendDialogue(string text, string author)
        {
            Send("dialogue", new[] { ("text", text), ("author", author ?? "") });
        }

        public void SendThought(string text, string emotion)
        {
            Send("thought", new[] { ("text", text), ("emotion", emotion ?? "") });
        }

        public void SendChoice(System.Collections.Generic.IList<string> options)
        {
            var sb = new StringBuilder();
            sb.Append("{\"t\":\"choice\",\"options\":[");
            for (int i = 0; i < options.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Quote(options[i]));
            }
            sb.Append("]}");
            SendRaw(sb.ToString());
        }

        public void SendPicked(string text)
        {
            Send("picked", new[] { ("text", text ?? "") });
        }

        public void SendInput(string text)
        {
            Send("input", new[] { ("text", text ?? "") });
        }

        public void SendInputSubmit(string text)
        {
            Send("inputSubmit", new[] { ("text", text ?? "") });
        }

        public void SendUI(string panel, string title, System.Collections.Generic.IList<string> buttons)
        {
            var sb = new StringBuilder();
            sb.Append("{\"t\":\"ui\",\"panel\":").Append(Quote(panel))
              .Append(",\"title\":").Append(Quote(title ?? ""));
            if (buttons != null && buttons.Count > 0)
            {
                sb.Append(",\"buttons\":[");
                for (int i = 0; i < buttons.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(Quote(buttons[i]));
                }
                sb.Append(']');
            }
            sb.Append('}');
            SendRaw(sb.ToString());
        }

        public void SendUIHide(string panel, string extra)
        {
            Send("uiHide", new[] { ("panel", panel), ("text", extra ?? "") });
        }

        public void SendFocus(string label)
        {
            Send("focus", new[] { ("label", label ?? "") });
        }

        public void SendClickResult(bool ok, string label, int attempts)
        {
            Send("clickResult", new[] { ("ok", ok ? "1" : "0"), ("label", label ?? ""), ("attempts", attempts.ToString()) });
        }

        public void SendScriptDumped(string scriptPath, int lineCount)
        {
            Send("scriptDumped", new[] { ("path", scriptPath ?? ""), ("lines", lineCount.ToString()) });
        }

        public void SendClickable(System.Collections.Generic.IList<ClickableItem> items)
        {
            var sb = new StringBuilder();
            sb.Append("{\"t\":\"clickable\",\"items\":[");
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var it = items[i];
                sb.Append("{\"label\":").Append(Quote(it.Label))
                  .Append(",\"x\":").Append(it.X.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))
                  .Append(",\"y\":").Append(it.Y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))
                  .Append('}');
            }
            sb.Append("]}");
            SendRaw(sb.ToString());
        }

        public void SendMode(string type, bool on)
        {
            Send(type, new[] { ("on", on ? "1" : "0") });
        }

        public void SendPlay(string script)
        {
            Send("play", new[] { ("script", script ?? "") });
        }

        public void SendStop()
        {
            SendRaw("{\"t\":\"stop\"}");
        }

        public void SendWaitingInput(bool on)
        {
            Send("wait", new[] { ("on", on ? "1" : "0") });
        }

        private void Send(string type, (string k, string v)[] fields)
        {
            var sb = new StringBuilder();
            sb.Append("{\"t\":").Append(Quote(type));
            foreach (var f in fields)
            {
                sb.Append(',').Append(Quote(f.k)).Append(':').Append(Quote(f.v));
            }
            sb.Append('}');
            SendRaw(sb.ToString());
        }

        private static string Quote(string s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder(s.Length + 8);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        private void SendRaw(string line)
        {
            lock (writeLock)
            {
                var s = stream;
                if (s == null) return; // not connected; silently drop
                try
                {
                    byte[] data = Encoding.UTF8.GetBytes(line + "\n");
                    s.Write(data, 0, data.Length);
                    s.Flush();
                }
                catch
                {
                    // connection broke; connection thread will reconnect
                }
            }
        }

        public void Dispose()
        {
            running = false;
            lock (writeLock)
            {
                if (stream != null) { try { stream.Dispose(); } catch { } stream = null; }
                if (client != null) { try { client.Close(); } catch { } client = null; }
            }
        }
    }
}
