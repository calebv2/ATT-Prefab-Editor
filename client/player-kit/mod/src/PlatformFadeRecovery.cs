using System;
using Alta.Alchemy;
using Alta.Chunks;
using HarmonyLib;
using UnityEngine;

namespace PrefabEditorMod
{
    // Native customization pads fade out for a scene transition. A linked pad
    // instead receives an in-world teleport, which may never release that fade
    // when its destination chunk was already preloaded.
    internal static class PlatformFadeRecovery
    {
        static HarmonyLib.Harmony harmony;
        static readonly System.Reflection.MethodInfo isLocked = AccessTools.Method(typeof(PlayerScreenFader), "IsFadeLocked");
        static readonly System.Reflection.FieldInfo teleportable = AccessTools.Field(typeof(EffectHandler), "isTeleportable");
        static PlayerController pending;
        static PlayerEffectController queuedEffect;
        static float queuedPower;
        static EffectHandler.TeleportType queuedType;
        static Vector3 destination;
        static bool receivedMove;
        static float deadline;
        static Action<string> log;

        internal static void Initialize(Action<string> logger)
        {
            log = logger;
            harmony = new HarmonyLib.Harmony("prefabeditor.platform-fade");
            try {
                harmony.Patch(AccessTools.Method(typeof(FromPlayingToCustomization), "ReturnToCustomization"),
                    prefix: new HarmonyMethod(typeof(PlatformFadeRecovery), "PlatformPrefix"));
                harmony.Patch(AccessTools.Method(typeof(PlayerEffectController), "Teleport"),
                    prefix: new HarmonyMethod(typeof(PlatformFadeRecovery), "TeleportPrefix"));
            } catch (Exception e) {
                harmony.UnpatchSelf();
                if (log != null) log("Platform fade recovery unavailable: " + e.Message);
            }
        }

        static void PlatformPrefix(PlayerController __0)
        {
            if (__0 == null || !__0.IsLocalPlayer || !__0.IsAlive) return;
            queuedEffect = null;
            pending = __0;
            receivedMove = false;
            deadline = Time.unscaledTime + 45f;
        }

        static bool TeleportPrefix(PlayerEffectController __instance, PlayerController ___player,
            bool ___isTeleportable, Vector3 __0, float __1, EffectHandler.TeleportType __2)
        {
            if (pending == null || ___player != pending || PlayerController.Current != pending ||
                !___player.IsLocalPlayer || !___player.IsAlive || Time.unscaledTime >= deadline) return true;
            destination = __0;
            if (!___isTeleportable) {
                // Native Teleport otherwise silently drops requests during its
                // cooldown. Retain only this platform activation's latest move.
                queuedEffect = __instance;
                queuedPower = __1;
                queuedType = __2;
                receivedMove = false;
                return false;
            }
            queuedEffect = null;
            receivedMove = true;
            return true;
        }

        internal static void Tick()
        {
            if (pending == null) return;
            if (PlayerController.Current != pending || !pending.IsAlive || Time.unscaledTime >= deadline) {
                pending = null;
                queuedEffect = null;
                return;
            }
            if (queuedEffect != null && (bool)teleportable.GetValue(queuedEffect)) {
                PlayerEffectController effect = queuedEffect;
                queuedEffect = null;
                effect.Teleport(destination,queuedPower,queuedType);
            }
            if (!receivedMove || (pending.transform.position - destination).sqrMagnitude > 1f) return;
            LocationChunk chunk = LocationChunkHelper.GetBestContaining(destination);
            if (chunk == null || !chunk.IsFullyLoaded || pending.ScreenFader == null || (bool)isLocked.Invoke(pending.ScreenFader, null)) return;
            // Respect every native fade locker; never force a loading screen open.
            pending.ScreenFader.FadeInIfNotLocked(.5f);
            pending = null;
            if (log != null) log("Linked platform arrival ready; released customization fade.");
        }

        internal static void Shutdown()
        {
            pending = null;
            queuedEffect = null;
            if (harmony != null) harmony.UnpatchSelf();
            harmony = null;
        }
    }
}
