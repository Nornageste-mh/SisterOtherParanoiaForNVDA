// SopAccess — 《妹妹、他人、妄想症》(Sister Other Paranoia) NVDA 无障碍辅助 · 游戏内插件入口
// BepInEx 5 plugin for Unity 6 + Naninovel.
using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace SopAccess
{
    [BepInPlugin("com.sop.access", "Sister Other Paranoia - NVDA Access", "1.2.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        private readonly EventSender sender = new EventSender();
        private readonly GameHooks hooks = new GameHooks();
        private bool hooked;
        private float nextPoll;

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo("SopAccess: plugin loaded, waiting for Naninovel engine...");
            // Make sure our hooks object links to the sender.
            hooks.AttachSender(sender);
            sender.OnCommand += OnCommand;
        }

        private void OnCommand(string type, int index)
        {
            try
            {
                hooks.HandleCommand(type, index);
            }
            catch (System.Exception e)
            {
                Log.LogWarning("SopAccess: command handling error: " + e.Message);
            }
        }

        private void Update()
        {
            if (!hooked)
            {
                if (Naninovel.Engine.Initialized)
                {
                    hooked = true;
                    try
                    {
                        hooks.Hook();
                        Log.LogInfo("SopAccess: Naninovel engine ready, hooks attached.");
                    }
                    catch (System.Exception e)
                    {
                        Log.LogError("SopAccess: failed to attach hooks: " + e);
                    }
                }
                return;
            }

            if (Time.unscaledTime >= nextPoll)
            {
                nextPoll = Time.unscaledTime + 0.2f;
                try { hooks.Poll(); }
                catch (System.Exception e) { Log.LogWarning("SopAccess: poll error: " + e.Message); }
            }
        }

        private void OnDestroy()
        {
            try
            {
                hooks.Unhook();
                sender.Dispose();
            }
            catch (System.Exception e)
            {
                Log.LogWarning("SopAccess: teardown error: " + e.Message);
            }
        }
    }
}
