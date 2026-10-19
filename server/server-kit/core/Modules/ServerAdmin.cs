using System;
using System.IO;
using System.Text;
using Alta.Console;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace PrefabEditor.Modules
{
    internal static class ServerSpawn
    {
        internal sealed class Location { public float X, Y, Z; }
        static Location location;
        static string file;
        static HarmonyLib.Harmony harmony;
        static string error;
        internal static bool Valid(float n) { return !float.IsNaN(n) && !float.IsInfinity(n); }
        internal static void Initialize(string directory, Action<string> log)
        {
            file = Path.Combine(directory, "server-spawn.json");
            try
            {
                location = File.Exists(file) ? JsonConvert.DeserializeObject<Location>(File.ReadAllText(file)) : null;
                if (location != null && (!Valid(location.X) || !Valid(location.Y) || !Valid(location.Z)))
                    throw new Exception("invalid saved spawn coordinates");
                harmony = new HarmonyLib.Harmony("prefabeditor.server-spawn");
                harmony.Patch(AccessTools.Method(typeof(SpawnArea), "GetSpawnPosition"),
                    prefix: new HarmonyMethod(typeof(ServerSpawn), "Prefix"));
                error = null;
            }
            catch (Exception e) { error = e.Message; location = null; log("Server spawn unavailable: " + error); }
        }
        static bool Prefix(SpawnArea __instance, int __0, ref Alta.Populations.SpawnPoint __result)
        {
            if (location == null || (__instance.Identifier != SpawnAreaIdentifier.RespawnPoint
                && __instance.Identifier != SpawnAreaIdentifier.OriginalSpawnPoint)) return true;
            __result = new Alta.Populations.SpawnPoint(new Vector3(location.X, location.Y, location.Z), Quaternion.identity, __0);
            return false;
        }
        internal static void Set(Location next)
        {
            if (error != null || file == null) throw new Exception(error ?? "spawn support not initialized");
            string temp = file + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(next));
            if (File.Exists(file)) File.Replace(temp, file, file + ".bak");
            else File.Move(temp, file);
            location = next;
        }
        internal static void Shutdown()
        {
            if (harmony != null) harmony.UnpatchSelf();
            location = null;
        }
    }

    public static partial class EditorAdminModule
    {
        [Command("setserverspawn", "Set persistent server spawn and respawn position: x y z")]
        public static string SetServerSpawn(float x, float y, float z)
        {
            if (!ServerSpawn.Valid(x) || !ServerSpawn.Valid(y) || !ServerSpawn.Valid(z)) return EditJson.Err("coordinates must be finite");
            try { ServerSpawn.Set(new ServerSpawn.Location { X=x, Y=y, Z=z }); return "{\"ok\":true,\"note\":\"Server spawn saved. Applies to new arrivals and default respawns; personal homes are unchanged.\"}"; }
            catch (Exception e) { return EditJson.Err("set server spawn: " + e.Message); }
        }
        [Command("resetserverspawn", "Restore the game's default server spawn locations")]
        public static string ResetServerSpawn()
        {
            try { ServerSpawn.Set(null); return "{\"ok\":true,\"note\":\"Default server spawn restored.\"}"; }
            catch (Exception e) { return EditJson.Err("reset server spawn: " + e.Message); }
        }
        [Command("broadcast", "Send a UTF-8 base64 encoded message to all online players")]
        public static string Broadcast(string encoded)
        {
            try
            {
                if (encoded == null || encoded.Length > 3000) return EditJson.Err("message is too long");
                string message = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(encoded));
                if (string.IsNullOrEmpty(message.Trim()) || message.Length > 500) return EditJson.Err("message must be 1-500 characters");
                if (PlayerCommunicationManager.Instance == null) return EditJson.Err("player messaging is unavailable");
                int count = 0;
                foreach (Player player in Player.AllPlayers)
                {
                    if (player == null || player.PlayerController == null) continue;
                    PlayerCommunicationManager.Instance.SendMessageToPlayer(player, message, 10f);
                    count++;
                }
                return "{\"ok\":true,\"note\":\"Broadcast sent to " + count + " online players.\"}";
            }
            catch (Exception e) { return EditJson.Err("broadcast: " + e.Message); }
        }
    }
}
