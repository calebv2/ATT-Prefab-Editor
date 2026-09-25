using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Alta.Console;             // ModuleAttribute, CommandAttribute, AliasAttribute
using Alta.Chunks;              // Chunk, LocationChunkHelper
using Alta.Inventory;           // PickupDock
using Alta.Networking;          // NetworkEntity, NetworkScene
using Alta.Networking.Internal; // EntityManager
using ATT.Saving;               // SaveMode
using Newtonsoft.Json;
using UnityEngine;
// NOTE: NetworkSceneManager, Pickup, Player, SavedDynamicObject,
// SerializedSavedDynamicObject live in the GLOBAL namespace
// (same story as GroundSweeper/CraftingBoxes).

// Blueprints: capture and place multi-entity save-string builds.
//
// Capture a build: every dynamic root entity within a radius of a player is
// exported as its save string (SavedDynamicObject.GetSaveOf(root).StringVersion())
// plus its offset from the capture center, stored as
// UserData\PrefabEditor\blueprints\<name>.json. Place it anywhere later: each
// string respawns via SerializedSavedDynamicObject.SpawnFromString, is moved to
// target + offset, and gets its chunk from GetBestContaining. Spawning is
// time-sliced (a few entities per frame) so a big blueprint never stalls the
// main thread.
//
// SAFETY (standing rule - mass-effect features preview first): `place` without
// the literal trailing word `confirm` only REPORTS what would spawn. Capture
// has an entity cap; placement runs through the same cap.
//
// What capture takes (and what it skips): entities with a Pickup - loose AND
// placed player items, containers (their docked contents ride along INSIDE the
// container's own save string), crafted objects. It skips docked/held items
// (already inside their parent's string / in someone's hand), environmental
// world spawns (trees, rocks), pickup-less world furniture, and player
// characters. Preview the capture breakdown before trusting it.

namespace PrefabEditor.Modules
{
    public sealed class BlueprintsModule : TavernModule
    {
        public override string Name { get { return "Blueprint"; } }

        protected override void Init()
        {
            BlueprintEngine.Boot(Ctx, Log);
            Log.Msg("'blueprint' module ready: capture/list/preview/place/status/delete/string. "
                + "Dir: " + BlueprintEngine.BlueprintDir);
        }

        public override void OnUpdate()
        {
            BlueprintEngine.Tick();
        }

        public override void Shutdown()
        {
            BlueprintEngine.CancelPlacement("shutdown");
        }
    }

    // ------------------------------------------------------------------
    //  Console surface: `blueprint ...`
    // ------------------------------------------------------------------

    [Module("blueprint", "Capture and place multi-entity blueprints (save-string based)")]
    public static class BlueprintAdminModule
    {
        [Command("capture", "Capture around a player: blueprint capture <name> <player> [radius=10]")]
        [Alias(new string[] { "cap" })]
        public static string Capture(string name, string playerName, float radius = 10f)
        {
            return BlueprintEngine.Capture(name, playerName, radius);
        }

        [Command("list", "List saved blueprints")]
        [Alias(new string[] { "ls" })]
        public static string List()
        {
            return BlueprintEngine.List();
        }

        [Command("preview", "Show a blueprint's entity breakdown without spawning")]
        [Alias(new string[] { "info" })]
        public static string Preview(string name)
        {
            return BlueprintEngine.Preview(name);
        }

        [Command("place", "Place at a player: blueprint place <name> <player> [confirm]. Preview-only without 'confirm'")]
        public static string Place(string name, string playerName, string confirm = null)
        {
            return BlueprintEngine.Place(name, playerName, confirm);
        }

        [Command("status", "Progress of the current placement")]
        public static string Status()
        {
            return BlueprintEngine.PlacementStatus();
        }

        [Command("cancel", "Cancel the current placement (already-spawned entities stay)")]
        public static string Cancel()
        {
            return BlueprintEngine.CancelPlacement("console cancel");
        }

        [Command("delete", "Delete a saved blueprint file")]
        [Alias(new string[] { "rm" })]
        public static string Delete(string name)
        {
            return BlueprintEngine.Delete(name);
        }

        // One-shot save-string export. The vanilla `select <id>` + `select
        // tostring` pair NREs over our REST console: each request gets a fresh
        // CommandService context, so the selection is gone by the second call
        // — a per-request REST console keeps no session. This does both halves in one invocation.
        [Command("string", "Print the save string of one entity: blueprint string <entityId>")]
        [Alias(new string[] { "tostring", "export" })]
        public static string ExportString(uint entityId)
        {
            return BlueprintEngine.ExportString(entityId);
        }
    }

    // ------------------------------------------------------------------
    //  The engine
    // ------------------------------------------------------------------

    static class BlueprintEngine
    {
        // -------------- file shapes (Newtonsoft POCOs) --------------

        public sealed class BlueprintEntity
        {
            public string prefab;
            public uint hash;
            public float dx, dy, dz;   // offset from capture center
            public string data;        // the save string
        }

        public sealed class BlueprintFile
        {
            public string name;
            public string capturedAt;  // ISO stamp, informational only
            public float radius;
            public int count;
            public List<BlueprintEntity> entities;
        }

        const int MaxEntities = 500;       // capture AND place cap
        const int SpawnsPerFrame = 3;      // time-slice budget
        const float PlaceHeightBump = 0.05f; // tiny lift so nothing spawns clipped

        static CoreContext _ctx;
        static ModLog _log;
        public static string BlueprintDir;

        // One placement at a time; consumed from OnUpdate on the main thread.
        sealed class Placement
        {
            public string Name;
            public Vector3 Target;
            public Queue<BlueprintEntity> Pending;
            public int Total;
            public int Spawned;
            public int Failed;
            public string FirstError;
        }
        static Placement _placing;

        public static void Boot(CoreContext ctx, ModLog log)
        {
            _ctx = ctx;
            _log = log;
            BlueprintDir = Path.Combine(ctx.CoreDir, "blueprints");
            try { Directory.CreateDirectory(BlueprintDir); }
            catch (Exception e) { log.Error("blueprint dir create failed: " + e.Message); }
        }

        // -------------- capture --------------

        public static string Capture(string name, string playerName, float radius)
        {
            string safe = SafeName(name);
            if (safe == null) return "Blueprint names: letters/digits/dash/underscore, 1-40 chars.";
            if (radius <= 0f || radius > 100f) return "Radius must be in (0, 100].";

            Player player = FindPlayer(playerName);
            if (player == null) return "No online player matching '" + playerName + "'.";
            Vector3 center;
            try { center = player.PlayerController.transform.position; }
            catch { return "Player '" + playerName + "' has no position (not spawned?)."; }

            List<BlueprintEntity> captured = new List<BlueprintEntity>();
            Dictionary<string, int> byPrefab = new Dictionary<string, int>();
            int skippedStatic = 0, skippedDocked = 0, failed = 0;

            foreach (NetworkEntity e in AllEntities())
            {
                if (e == null || e.Parent != null || e.IsBeingDestroyed) continue;

                Vector3 pos;
                try { pos = e.transform.position; } catch { continue; }
                if (Vector3.Distance(pos, center) > radius) continue;

                string prefabName = (e.Prefab != null) ? e.Prefab.name : e.name;
                string norm = Normalize(prefabName);
                // never capture people
                if (norm.IndexOf("player", StringComparison.Ordinal) >= 0
                    || norm.IndexOf("character", StringComparison.Ordinal) >= 0) continue;

                // Scope: the point of a
                // blueprint is CONTAINERS-WITH-CONTENTS and placed builds, plus
                // whatever loose items are part of the arrangement.
                //  - Pickup-bearing entities: capture when free (not docked into
                //    anything, not in a hand, not environmental). Docked things
                //    ride INSIDE their container's save string - capturing them
                //    separately would duplicate them. IsChunkingParentStatic is
                //    allowed: placed builds are static-parented on purpose.
                //  - Pickup-LESS entities (placed furniture): capture IF they
                //    carry PickupDocks (chests, crates, racks - the save string
                //    carries their contents). Dock-less world furniture and
                //    environmental spawns (trees/rocks) stay out.
                // Capture is non-destructive and place is preview-gated, so the
                // per-prefab breakdown is the guard against surprises.
                Pickup p = e.CommonPickup;
                if (p != null)
                {
                    if (p.IsDocked || p.IsInteractedWith) { skippedDocked++; continue; }
                    if (p.IsEnvironmental) { skippedStatic++; continue; }
                    // Mounted parts (guard on stick, attachment on guard: a
                    // Unity Joint ON the mounted item) ride inside their base
                    // item's save string like docked contents do - capturing
                    // them separately would duplicate them on place.
                    if (IsMounted(e)) { skippedDocked++; continue; }
                }
                else
                {
                    PickupDock[] docks = null;
                    try { docks = e.GetComponentsInChildren<PickupDock>(true); } catch { }
                    if (docks == null || docks.Length == 0) { skippedStatic++; continue; }
                }

                string s;
                try
                {
                    s = SavedDynamicObject.GetSaveOf(e).StringVersion();
                }
                catch (Exception ex)
                {
                    failed++;
                    if (failed <= 3) _log.Warning("capture: GetSaveOf failed for " + prefabName + ": " + ex.Message);
                    continue;
                }
                if (string.IsNullOrEmpty(s)) { failed++; continue; }

                BlueprintEntity be = new BlueprintEntity();
                be.prefab = prefabName;
                be.hash = (e.Prefab != null) ? e.Prefab.Hash : 0;
                be.dx = pos.x - center.x;
                be.dy = pos.y - center.y;
                be.dz = pos.z - center.z;
                be.data = s;
                captured.Add(be);

                int c;
                byPrefab.TryGetValue(prefabName, out c);
                byPrefab[prefabName] = c + 1;

                if (captured.Count > MaxEntities)
                    return "Capture aborted: more than " + MaxEntities + " entities in radius "
                        + radius + " - shrink the radius.";
            }

            if (captured.Count == 0)
                return "Nothing captureable within " + radius + " of " + playerName
                    + " (" + skippedStatic + " static, " + skippedDocked + " docked/held skipped).";

            BlueprintFile file = new BlueprintFile();
            file.name = safe;
            file.capturedAt = DateTime.UtcNow.ToString("o");
            file.radius = radius;
            file.count = captured.Count;
            file.entities = captured;
            try
            {
                File.WriteAllText(PathFor(safe), JsonConvert.SerializeObject(file, Formatting.Indented));
            }
            catch (Exception e)
            {
                return "Capture ok but save FAILED: " + e.Message;
            }

            return "CAPTURED '" + safe + "': " + captured.Count + " entities (radius " + radius
                + ", " + skippedStatic + " dock-less furniture/environmental + "
                + skippedDocked + " docked/held skipped, "
                + failed + " export failures)\n" + Breakdown(byPrefab)
                + "\nPreview any time: blueprint preview " + safe;
        }

        // -------------- list / preview / delete --------------

        public static string List()
        {
            try
            {
                string[] files = Directory.GetFiles(BlueprintDir, "*.json");
                if (files.Length == 0) return "No blueprints saved. blueprint capture <name> <player> [radius]";
                StringBuilder sb = new StringBuilder();
                sb.Append(files.Length).Append(" blueprint(s):");
                foreach (string f in files)
                {
                    BlueprintFile bp = LoadFile(Path.GetFileNameWithoutExtension(f));
                    sb.Append("\n  ").Append(Path.GetFileNameWithoutExtension(f));
                    if (bp != null) sb.Append("  (").Append(bp.count).Append(" entities, radius ").Append(bp.radius).Append(")");
                    else sb.Append("  (UNREADABLE)");
                }
                return sb.ToString();
            }
            catch (Exception e)
            {
                return "list failed: " + e.Message;
            }
        }

        public static string Preview(string name)
        {
            BlueprintFile bp = LoadFile(SafeName(name));
            if (bp == null) return "No blueprint '" + name + "'.";
            Dictionary<string, int> byPrefab = new Dictionary<string, int>();
            foreach (BlueprintEntity e in bp.entities)
            {
                int c;
                byPrefab.TryGetValue(e.prefab, out c);
                byPrefab[e.prefab] = c + 1;
            }
            return "BLUEPRINT '" + bp.name + "': " + bp.count + " entities, captured " + bp.capturedAt
                + "\n" + Breakdown(byPrefab);
        }

        public static string Delete(string name)
        {
            string safe = SafeName(name);
            if (safe == null) return "Bad name.";
            string path = PathFor(safe);
            if (!File.Exists(path)) return "No blueprint '" + safe + "'.";
            try { File.Delete(path); return "Deleted blueprint '" + safe + "'."; }
            catch (Exception e) { return "delete failed: " + e.Message; }
        }

        // Assembly mounts are Unity Joints living ON the mounted part
        // (EmbeddableSurface idiom) - but only joints that anchor OUTSIDE this
        // root count. Internal articulation (a chest's own lid hinge) is not a
        // mount; the old any-joint test skipped every lidded container from
        // captures, which is exactly the containers-with-contents case the
        // module exists for. On error, claim mounted - skipping is safe.
        static bool IsMounted(NetworkEntity root)
        {
            try
            {
                Joint[] joints = root.GetComponentsInChildren<Joint>(true);
                if (joints == null) return false;
                for (int i = 0; i < joints.Length; i++)
                {
                    Joint j = joints[i];
                    if (j == null) continue;
                    Rigidbody other = j.connectedBody;
                    if (other == null) return true;
                    NetworkEntity oe = other.GetComponentInParent<NetworkEntity>();
                    NetworkEntity oroot = (oe != null && oe.PrefabRoot != null) ? oe.PrefabRoot : oe;
                    if (oroot != root) return true;
                }
                return false;
            }
            catch { return true; }
        }

        // -------------- one-shot string export --------------

        public static string ExportString(uint entityId)
        {
            foreach (NetworkEntity e in AllEntities())
            {
                if (e == null || e.Identifier != entityId) continue;
                if (e.IsBeingDestroyed) return "Entity " + entityId + " is being destroyed.";
                string prefabName = (e.Prefab != null) ? e.Prefab.name : e.name;
                try
                {
                    string s = SavedDynamicObject.GetSaveOf(e).StringVersion();
                    if (string.IsNullOrEmpty(s)) return "GetSaveOf returned nothing for " + prefabName + ".";
                    // Name line first, raw string LAST - tools take the last line.
                    return prefabName + " (" + entityId + "):\n" + s;
                }
                catch (Exception ex)
                {
                    return "Export failed for " + prefabName + ": " + ex.Message;
                }
            }
            return "No entity with id " + entityId + " (loaded chunks only - stand near it, re-check the id with select find).";
        }

        // -------------- place --------------

        public static string Place(string name, string playerName, string confirm)
        {
            if (_placing != null)
                return "A placement is already running (" + PlacementStatus() + "). blueprint cancel to stop it.";

            BlueprintFile bp = LoadFile(SafeName(name));
            if (bp == null) return "No blueprint '" + name + "'.";
            if (bp.entities == null || bp.entities.Count == 0) return "Blueprint '" + name + "' is empty.";
            if (bp.entities.Count > MaxEntities) return "Blueprint exceeds the " + MaxEntities + " entity cap.";

            Player player = FindPlayer(playerName);
            if (player == null) return "No online player matching '" + playerName + "'.";
            Vector3 target;
            try { target = player.PlayerController.transform.position; }
            catch { return "Player '" + playerName + "' has no position (not spawned?)."; }

            // Preview-first is the contract: no literal 'confirm', no spawns.
            if (confirm == null || confirm.Trim().ToLowerInvariant() != "confirm")
            {
                return Preview(name) + "\nTarget: " + playerName + " @ " + Fmt(target)
                    + "\nNothing spawned. Append 'confirm' to place: blueprint place "
                    + SafeName(name) + " " + playerName + " confirm";
            }

            Placement pl = new Placement();
            pl.Name = bp.name;
            pl.Target = target;
            pl.Pending = new Queue<BlueprintEntity>(bp.entities);
            pl.Total = bp.entities.Count;
            _placing = pl;
            _log.Msg("placing '" + bp.name + "': " + pl.Total + " entities at " + Fmt(target)
                + " (" + SpawnsPerFrame + "/frame).");
            return "PLACING '" + bp.name + "': " + pl.Total + " entities queued at " + Fmt(target)
                + ". Watch: blueprint status";
        }

        public static string PlacementStatus()
        {
            Placement pl = _placing;
            if (pl == null) return "No placement running.";
            return "'" + pl.Name + "': " + pl.Spawned + " spawned, " + pl.Failed + " failed, "
                + pl.Pending.Count + " pending of " + pl.Total
                + (pl.FirstError != null ? " (first error: " + pl.FirstError + ")" : "");
        }

        public static string CancelPlacement(string why)
        {
            Placement pl = _placing;
            if (pl == null) return "No placement running.";
            _placing = null;
            _log.Msg("placement '" + pl.Name + "' cancelled (" + why + "): "
                + pl.Spawned + "/" + pl.Total + " had spawned.");
            return "Cancelled '" + pl.Name + "' (" + pl.Spawned + "/" + pl.Total
                + " already spawned - they stay; sweep or wacky destroy to remove).";
        }

        // Main thread, every frame. Spawn a small batch of the active placement.
        public static void Tick()
        {
            Placement pl = _placing;
            if (pl == null) return;

            for (int i = 0; i < SpawnsPerFrame; i++)
            {
                if (pl.Pending.Count == 0)
                {
                    _placing = null;
                    _log.Msg("placement '" + pl.Name + "' DONE: " + pl.Spawned + " spawned, "
                        + pl.Failed + " failed of " + pl.Total + ".");
                    return;
                }
                BlueprintEntity be = pl.Pending.Dequeue();
                try
                {
                    Vector3 pos = new Vector3(pl.Target.x + be.dx,
                                              pl.Target.y + be.dy + PlaceHeightBump,
                                              pl.Target.z + be.dz);
                    // COMPILE/LIVE CHECK POINT: 11.4 records the call + "then set
                    // .Chunk" - if SpawnFromString returns something other than
                    // NetworkEntity, adapt here (the repositioning below is the
                    // part that must survive: strings encode their ORIGINAL spot).
                    NetworkEntity spawned = SerializedSavedDynamicObject.SpawnFromString(be.data, SaveMode.Base);
                    if (spawned == null) { Fail(pl, be, "SpawnFromString returned null"); continue; }
                    spawned.transform.position = pos;
                    spawned.Chunk = LocationChunkHelper.GetBestContaining(pos);
                    // broadcast the reposition - the spawn itself went out at the
                    // string's ORIGINAL coordinates (same idiom as edit move)
                    spawned.ForcePositionSync();
                    pl.Spawned++;
                }
                catch (Exception e)
                {
                    Fail(pl, be, e.Message);
                }
            }
        }

        static void Fail(Placement pl, BlueprintEntity be, string why)
        {
            pl.Failed++;
            if (pl.FirstError == null) pl.FirstError = be.prefab + ": " + why;
            if (pl.Failed <= 3) _log.Warning("place '" + pl.Name + "': " + be.prefab + " failed: " + why);
        }

        // -------------- helpers --------------

        static string PathFor(string safe)
        {
            return Path.Combine(BlueprintDir, safe + ".json");
        }

        static BlueprintFile LoadFile(string safe)
        {
            if (safe == null) return null;
            try
            {
                string path = PathFor(safe);
                if (!File.Exists(path)) return null;
                return JsonConvert.DeserializeObject<BlueprintFile>(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                _log.Warning("blueprint '" + safe + "' unreadable: " + e.Message);
                return null;
            }
        }

        static string SafeName(string name)
        {
            if (name == null) return null;
            string t = name.Trim().ToLowerInvariant();
            if (t.Length == 0 || t.Length > 40) return null;
            foreach (char c in t)
                if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_')) return null;
            return t;
        }

        static Player FindPlayer(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string needle = Normalize(name);
            Player prefix = null;
            foreach (Player p in Player.AllPlayers)
            {
                string u;
                try { u = p.UserInfo.Username; } catch { continue; }
                string norm = Normalize(u);
                if (norm == needle) return p;
                if (prefix == null && norm.StartsWith(needle, StringComparison.Ordinal)) prefix = p;
            }
            return prefix;
        }

        static string Breakdown(Dictionary<string, int> byPrefab)
        {
            List<KeyValuePair<string, int>> sorted = new List<KeyValuePair<string, int>>(byPrefab);
            sorted.Sort(delegate(KeyValuePair<string, int> a, KeyValuePair<string, int> b)
            {
                int cmp = b.Value.CompareTo(a.Value);
                return cmp != 0 ? cmp : string.CompareOrdinal(a.Key, b.Key);
            });
            StringBuilder sb = new StringBuilder();
            int shown = Math.Min(sorted.Count, 30);
            for (int i = 0; i < shown; i++)
            {
                if (i > 0) sb.Append("\n");
                sb.Append("  ").Append(sorted[i].Value.ToString().PadLeft(4)).Append("  ").Append(sorted[i].Key);
            }
            if (sorted.Count > shown) sb.Append("\n  ... and " + (sorted.Count - shown) + " more prefab(s)");
            return sb.ToString();
        }

        static string Fmt(Vector3 v)
        {
            return v.x.ToString("0.0") + "," + v.y.ToString("0.0") + "," + v.z.ToString("0.0");
        }

        static string Normalize(string s)
        {
            if (s == null) return "";
            char[] buffer = new char[s.Length];
            int n = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == ' ' || c == '-' || c == '_' || c == '\'') continue;
                buffer[n++] = char.ToLowerInvariant(c);
            }
            return new string(buffer, 0, n);
        }

        // Same scene-wide walk as sweep/craftbox (duplicated deliberately -
        // modules stay independently toggleable).
        static FieldInfo _entityManagerField;

        static IEnumerable<NetworkEntity> AllEntities()
        {
            NetworkScene scene = NetworkSceneManager.Current as NetworkScene;
            if (scene != null)
            {
                if (_entityManagerField == null)
                    _entityManagerField = typeof(NetworkScene).GetField("entityManager",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                if (_entityManagerField != null)
                {
                    EntityManager em = _entityManagerField.GetValue(scene) as EntityManager;
                    if (em != null) return em.IterateEntities();
                }
            }
            List<NetworkEntity> result = new List<NetworkEntity>();
            foreach (Chunk chunk in Chunk.ChunksByIndex.Values)
            {
                if (chunk == null || chunk.Entities == null) continue;
                foreach (NetworkEntity e in chunk.Entities.Entities) result.Add(e);
            }
            return result;
        }
    }
}
