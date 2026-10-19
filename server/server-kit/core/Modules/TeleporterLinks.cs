using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Alta.Networking;
using Alta.Networking.Scripts.Player;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace PrefabEditor.Modules
{
    // Customization platforms have no TeleportationPoint. Intercept their existing
    // server request, preserving the platform animation and native client loading.
    internal static class TeleporterLinks
    {
        internal sealed class Link
        {
            public uint Hash;
            public float SourceX, SourceY, SourceZ;
            public float X, Y, Z;
        }
        const string PatchId = "prefabeditor.teleporter-links";
        static List<Link> links = new List<Link>();
        static readonly Dictionary<NetworkEntity, Link> bound = new Dictionary<NetworkEntity, Link>();
        sealed class ContactTravel { public NetworkEntity Root; public float Until; }
        static readonly Dictionary<PlayerController, ContactTravel> contacts = new Dictionary<PlayerController, ContactTravel>();
        static string file;
        static string unavailable = "teleporter support has not initialized";
        static Action<string> log;
        static HarmonyLib.Harmony harmony;

        internal static void Initialize(string directory, Action<string> logger)
        {
            log = logger;
            file = Path.Combine(directory, "teleporter-links.json");
            try
            {
                List<Link> loaded = File.Exists(file) ? JsonConvert.DeserializeObject<List<Link>>(File.ReadAllText(file)) : new List<Link>();
                if (loaded == null) throw new Exception("link file is empty or invalid");
                foreach (Link link in loaded)
                    if (link == null || !Valid(link)) throw new Exception("link file contains invalid coordinates");
                links = loaded;
                bound.Clear();
                harmony = new HarmonyLib.Harmony(PatchId);
                MethodInfo sync = AccessTools.Method(typeof(FromPlayingToCustomization), "SyncPlayer");
                MethodInfo local = AccessTools.Method(typeof(FromPlayingToCustomization), "ReturnToCustomization");
                if (sync == null || local == null) throw new Exception("this game build has no customization platform hooks");
                harmony.Patch(sync, new HarmonyMethod(typeof(TeleporterLinks), "SyncPrefix"));
                harmony.Patch(local, new HarmonyMethod(typeof(TeleporterLinks), "LocalPrefix"));
                harmony.Patch(AccessTools.Method(typeof(TeleporterPlatform), "SyncPressing"),
                    postfix: new HarmonyMethod(typeof(TeleporterLinks), "ContactPostfix"));
                harmony.Patch(AccessTools.Method(typeof(TeleporterPlatform), "Entered"),
                    postfix: new HarmonyMethod(typeof(TeleporterLinks), "LocalContactPostfix"));
                harmony.Patch(AccessTools.Method(typeof(TeleporterPlatform), "Exited"),
                    postfix: new HarmonyMethod(typeof(TeleporterLinks), "LocalExitPostfix"));
                unavailable = null;
            }
            catch (Exception e)
            {
                unavailable = e.Message;
                if (harmony != null) harmony.UnpatchSelf();
                if (log != null) log("Platform linking unavailable: " + unavailable);
            }
        }

        internal static void Shutdown()
        {
            if (harmony != null) harmony.UnpatchSelf();
            harmony = null;
            bound.Clear();
            contacts.Clear();
            links.Clear();
            unavailable = "teleporter support has stopped";
        }

        static bool Finite(float n) { return !float.IsNaN(n) && !float.IsInfinity(n) && Math.Abs(n) <= 100000; }
        static bool Valid(Link l)
        {
            return l.Hash != 0 && Finite(l.SourceX) && Finite(l.SourceY) && Finite(l.SourceZ)
                && Finite(l.X) && Finite(l.Y) && Finite(l.Z);
        }
        static NetworkEntity Root(FromPlayingToCustomization platform)
        {
            NetworkEntity entity = platform.Entity;
            return entity == null ? null : (entity.PrefabRoot != null ? entity.PrefabRoot : entity);
        }
        static Link Find(NetworkEntity root)
        {
            if (root == null || root.Prefab == null) return null;
            Vector3 p = root.transform.position;
            Link match = null;
            foreach (Link link in links)
            {
                if (link.Hash != root.Prefab.Hash) continue;
                Vector3 delta = p - new Vector3(link.SourceX, link.SourceY, link.SourceZ);
                if (delta.sqrMagnitude > 0.0004f) continue;
                if (match != null) return null; // Never guess between overlapping records.
                match = link;
            }
            return match;
        }

        internal static string Set(NetworkEntity root, Vector3 destination)
        {
            if (unavailable != null) return "platform linking unavailable: " + unavailable;
            if (root.Prefab == null) return "selected platform has no network prefab";
            Vector3 p = root.transform.position;
            Link next = new Link { Hash = root.Prefab.Hash, SourceX = p.x, SourceY = p.y, SourceZ = p.z,
                X = destination.x, Y = destination.y, Z = destination.z };
            if (!Valid(next)) return "platform or destination has invalid coordinates";
            Link previous;
            if (!bound.TryGetValue(root, out previous)) previous = Find(root);
            List<Link> replacement = new List<Link>(links);
            if (previous != null) replacement.Remove(previous);
            // Also replace any record already stored at the new location.
            Link atPosition = Find(root);
            if (atPosition != null) replacement.Remove(atPosition);
            replacement.Add(next);
            string temp = file + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllText(temp, JsonConvert.SerializeObject(replacement, Formatting.Indented));
                if (File.Exists(file)) File.Replace(temp, file, file + ".bak");
                else File.Move(temp, file);
            }
            catch (Exception e) { return "could not save teleporter link: " + e.Message; }
            links = replacement;
            bound[root] = next;
            return null;
        }

        static bool Redirect(FromPlayingToCustomization platform, PlayerController player)
        {
            if (unavailable != null || !NetworkSceneManager.IsServer || player == null || !player.IsAlive) return false;
            NetworkEntity root = Root(platform);
            Link link = Find(root);
            if (link == null) return false;
            ContactTravel contact;
            // The native countdown/fade can finish after our contact trip. Consume
            // that late activation rather than teleporting twice or opening customization.
            if (contacts.TryGetValue(player, out contact) && contact.Root == root && Time.unscaledTime < contact.Until) return true;
            bound[root] = link;
            // SafeTeleport gates this call on native client chunk acknowledgement
            // and a server floor check before PlayerEffectController moves the client.
            player.ForceTeleportTo(new Vector3(link.X, link.Y, link.Z));
            return true;
        }
        static void Contact(TeleporterPlatform platform, PlayerController player, bool pressed)
        {
            if (!NetworkSceneManager.IsServer || player == null || !player.IsAlive || unavailable != null) return;
            if (!pressed) { SafeTeleport.CancelPreload(player, platform); return; }
            NetworkEntity entity = platform.Entity;
            NetworkEntity root = entity == null ? null : (entity.PrefabRoot ?? entity);
            Link link = Find(root);
            if (link == null) return;
            try {
                if (!SafeTeleport.Preload(player, new Vector3(link.X, link.Y, link.Z), platform)) return;
                foreach (var item in new List<KeyValuePair<PlayerController, ContactTravel>>(contacts))
                    if (item.Key == null || Time.unscaledTime >= item.Value.Until) contacts.Remove(item.Key);
                contacts[player] = new ContactTravel { Root = root, Until = Time.unscaledTime + 60f };
            }
            catch (Exception e) { if (log != null) log("Contact preload unavailable: " + e.Message); }
        }
        static void ContactPostfix(TeleporterPlatform __instance, IPlayer __0, Alta.Serialization.Stream __1,
            HashSet<IPlayer> ___pressing)
        {
            if (__1.IsReading && __0 != null) Contact(__instance, __0.PlayerController, ___pressing.Contains(__0));
        }
        static void LocalContactPostfix(TeleporterPlatform __instance, PlayerController ___localCharacter, bool ___isLocalPressing)
        {
            if (___isLocalPressing) Contact(__instance, ___localCharacter, true);
        }
        static void LocalExitPostfix(TeleporterPlatform __instance, bool ___isLocalPressing)
        {
            if (!___isLocalPressing && Player.Current != null) Contact(__instance, Player.Current.PlayerController, false);
        }
        static bool SyncPrefix(FromPlayingToCustomization __instance, IPlayer __0, Alta.Serialization.Stream __1)
        {
            if (!__1.IsReading || !NetworkSceneManager.IsServer) return true;
            return !Redirect(__instance, __0 == null ? null : __0.PlayerController);
        }
        static bool LocalPrefix(FromPlayingToCustomization __instance, PlayerController __0)
        {
            if (__0 == null || !__0.IsLocalPlayer || !NetworkSceneManager.IsServer) return true;
            return !Redirect(__instance, __0);
        }
    }
}
