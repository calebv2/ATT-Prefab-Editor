using System;
using System.Collections.Generic;
using System.Reflection;
using Alta.Console;      // ModuleAttribute, CommandAttribute, AliasAttribute
using Alta.Chunks;       // Chunk, LocationChunkHelper
using Alta.Networking;   // NetworkEntity, NetworkScene
using Alta.Networking.Internal; // EntityManager
using ATT.Saving;        // SaveMode (paste)
using UnityEngine;       // Vector3, Physics, Joint, Rigidbody
// NOTE: NetworkSceneManager, Player, Pickup and SerializedSavedDynamicObject
// live in the GLOBAL namespace.

// The prefab editor's server half: JSON one-shot commands the web editor (panel
// /editor) drives. Same move machinery as the native `select` module - a plain
// transform write plus ForcePositionSync (chunk re-eval + MoveEntity broadcast,
// SelectionCommandModule idiom) - but one-shot by entity id, so nothing depends
// on the console's per-request Selection (the NRE-over-REST trap the panel hit
// with `select` two-steps).
//
//   edit players                       who's on, with positions (camera anchors)
//   edit scan <player> [radius]        root entities near a player, JSON
//   edit scanat <x> <y> <z> [radius]   same, near a point (plots, multi-anchor)
//   edit info <id>                     one entity
//   edit move <id> <x> <y> <z>         set position (root), sync, undoable
//   edit movegroup <ids> <dx> <dy> <dz> move up to 100 roots together, sync
//   edit rotategroup <ids> <ex> <ey> <ez> rotate roots around the first root
//   edit arrangegroup <ids> <axis> <mode> <spacing> align / space a selection
//   edit rotate <id> <ex> <ey> <ez>    set euler rotation, sync, undoable
//   edit transform / scale              set both transforms / encoded scale
//   edit ground <id>                   raycast down, drop onto the ground
//   edit delete <id> [confirm]         two-step delete (bare call only reports)
//   edit deletegroup / duplicategroup   checked batch actions
//   edit undo / redo / history          shared in-memory editor history
//   edit paste <x> <y> <z> <string>    respawn an exported save string at a point
//   edit spawnat <x> <y> <z> <prefab>  spawn a catalog prefab (hash or name) at a point
//
// Entities only exist near players (chunk loading) - a scan away from
// everyone comes back empty by design. Docked and held items are skipped or
// vetoed: their position belongs to the dock/hand that owns them. Mounted parts
// (Unity Joint) are shown but refuse to move alone -
// move the base item they ride on.

namespace PrefabEditor.Modules
{
    public sealed class EditorModule : TavernModule
    {
        public override string Name { get { return "Editor"; } }

        protected override void Init()
        {
            Log.Msg("'edit' module ready: players, scan <player> [radius], scanat x y z [radius], "
                + "info/move/rotate/transform/scale/ground <id>, delete/deletegroup, "
                + "duplicategroup, arrangegroup, undo/redo/history, paste x y z <string>, "
                + "spawnat x y z <prefab>. JSON out; the panel's /editor page and the client "
                + "editor are the front ends.");
        }
    }

    // Addressed as `edit ...` on the console.
    [Module("edit", "Prefab editor one-shots: scan (JSON), move, rotate, ground")]
    public static class EditorAdminModule
    {
        const int MaxEntities = 400;
        const float MaxRadius = 200f;
        const int MaxHistoryEntries = 64;
        const int MaxHistoryGroup = 300;
        const int MaxHistoryEntryBytes = 8 * 1024 * 1024;
        const long MaxHistoryBytes = 32L * 1024L * 1024L;

        sealed class HistorySnapshot
        {
            public uint Id;
            public bool Exists;
            public string SaveString;
            public Vector3 Position;
            public Quaternion Rotation;
        }

        sealed class HistoryEntry
        {
            public string Label;
            public int Bytes;
            public readonly List<HistorySnapshot> Before = new List<HistorySnapshot>();
            public readonly List<HistorySnapshot> After = new List<HistorySnapshot>();
        }

        static readonly List<HistoryEntry> _undo = new List<HistoryEntry>();
        static readonly List<HistoryEntry> _redo = new List<HistoryEntry>();
        static long _historyBytes;

        static FieldInfo _entityManagerField;

        // ---------------------------------------------------------------
        //  Commands
        // ---------------------------------------------------------------

        [Command("history", "Show available editor undo and redo steps")]
        public static string History()
        {
            return HistoryState();
        }

        [Command("undo", "Undo the last prefab editor action")]
        public static string Undo()
        {
            if (_undo.Count == 0) return EditJson.Err("nothing to undo");
            HistoryEntry entry = _undo[_undo.Count - 1];
            string err = ApplyHistory(entry, true);
            if (err != null) return EditJson.Err("undo stopped: " + err);
            _undo.RemoveAt(_undo.Count - 1);
            _redo.Add(entry);
            return HistoryAction("undid " + entry.Label, entry.Before);
        }

        [Command("redo", "Redo the last undone prefab editor action")]
        public static string Redo()
        {
            if (_redo.Count == 0) return EditJson.Err("nothing to redo");
            HistoryEntry entry = _redo[_redo.Count - 1];
            string err = ApplyHistory(entry, false);
            if (err != null) return EditJson.Err("redo stopped: " + err);
            _redo.RemoveAt(_redo.Count - 1);
            _undo.Add(entry);
            TrimHistory();
            return HistoryAction("redid " + entry.Label, entry.After);
        }

        [Command("players", "List connected players with positions (JSON)")]
        public static string Players()
        {
            List<string> records = new List<string>();
            try
            {
                foreach (Player p in Player.AllPlayers)
                {
                    if (p == null || p.PlayerController == null) continue;
                    Vector3 pos = p.PlayerController.transform.position;
                    float yaw = 0f;
                    try { yaw = p.PlayerController.Head.eulerAngles.y; } catch { }
                    records.Add("{\"name\":\"" + EditJson.Esc(SafeUsername(p)) + "\""
                        + ",\"x\":" + EditJson.F(pos.x) + ",\"y\":" + EditJson.F(pos.y)
                        + ",\"z\":" + EditJson.F(pos.z) + ",\"yaw\":" + EditJson.F(yaw) + "}");
                }
            }
            catch (Exception e) { return EditJson.Err("players: " + e.Message); }
            return "{\"ok\":true,\"players\":[" + string.Join(",", records.ToArray()) + "]}";
        }

        [Command("scan", "List root entities near a player as JSON")]
        public static string Scan(Player player, float radius = 30f)
        {
            if (player == null || player.PlayerController == null)
                return EditJson.Err("player not found or has no body yet");
            Vector3 anchor = player.PlayerController.transform.position;
            float yaw = 0f;
            try { yaw = player.PlayerController.Head.eulerAngles.y; } catch { }
            string playerJson = "{\"name\":\"" + EditJson.Esc(SafeUsername(player)) + "\""
                + ",\"x\":" + EditJson.F(anchor.x) + ",\"y\":" + EditJson.F(anchor.y)
                + ",\"z\":" + EditJson.F(anchor.z) + ",\"yaw\":" + EditJson.F(yaw) + "}";
            return ScanAround(anchor, radius, playerJson);
        }

        [Command("scanat", "List root entities near a point as JSON")]
        public static string ScanAt(float x, float y, float z, float radius = 30f)
        {
            return ScanAround(new Vector3(x, y, z), radius, null);
        }

        [Command("info", "One entity as JSON")]
        public static string Info(uint id)
        {
            NetworkEntity root;
            string err = ResolveRoot(id, out root);
            if (err != null) return EditJson.Err(err);
            return "{\"ok\":true,\"entity\":" + EntityRecord(root, root.transform.position) + "}";
        }

        [Command("move", "Set an entity's position (acts on the prefab root)")]
        public static string Move(uint id, float x, float y, float z)
        {
            NetworkEntity root;
            string err = ResolveRoot(id, out root);
            if (err == null) err = VetoMove(root);
            if (err != null) return EditJson.Err(err);
            HistorySnapshot before = CaptureTransform(root);
            try
            {
                root.transform.position = new Vector3(x, y, z);
                StopMomentum(root);
                root.ForcePositionSync();
            }
            catch (Exception e) { return EditJson.Err("move: " + e.Message); }
            CommitTransform("move", new List<HistorySnapshot> { before }, new List<HistorySnapshot> { CaptureTransform(root) });
            return Placed(root);
        }

        [Command("transform", "Set position and rotation together: edit transform <id> <x> <y> <z> <ex> <ey> <ez>")]
        public static string Transform(uint id, float x, float y, float z, float ex, float ey, float ez)
        {
            NetworkEntity root;
            string err = ResolveRoot(id, out root);
            if (err == null) err = VetoMove(root);
            if (err != null) return EditJson.Err(err);
            HistorySnapshot before = CaptureTransform(root);
            try
            {
                root.transform.position = new Vector3(x, y, z);
                root.transform.eulerAngles = new Vector3(ex, ey, ez);
                StopMomentum(root);
                root.Chunk = LocationChunkHelper.GetBestContaining(root.transform.position);
                root.ForcePositionSync();
            }
            catch (Exception e)
            {
                try { root.transform.position = before.Position; root.transform.rotation = before.Rotation; root.ForcePositionSync(); } catch { }
                return EditJson.Err("transform: " + e.Message);
            }
            CommitTransform("transform", new List<HistorySnapshot> { before }, new List<HistorySnapshot> { CaptureTransform(root) });
            return Placed(root);
        }

        [Command("movegroup", "Move 2 to 100 entity roots together: edit movegroup <comma-separated ids> <dx> <dy> <dz>")]
        public static string MoveGroup(string ids, float dx, float dy, float dz)
        {
            if (string.IsNullOrEmpty(ids)) return EditJson.Err("no selected entity ids");
            string[] parts = ids.Split(',');
            if (parts.Length < 2 || parts.Length > 100) return EditJson.Err("group size must be between 2 and 100");
            List<NetworkEntity> roots = new List<NetworkEntity>();
            List<Vector3> starts = new List<Vector3>();
            List<HistorySnapshot> before = new List<HistorySnapshot>();
            HashSet<uint> seen = new HashSet<uint>();
            for (int i = 0; i < parts.Length; i++)
            {
                uint id;
                if (!uint.TryParse(parts[i], out id)) return EditJson.Err("invalid entity id in group");
                if (!seen.Add(id)) continue;
                NetworkEntity root;
                string err = ResolveRoot(id, out root);
                if (err == null) err = VetoMove(root);
                if (err != null) return EditJson.Err("group move refused before moving anything: " + err);
                if (roots.Contains(root)) continue;
                roots.Add(root);
                starts.Add(root.transform.position);
                before.Add(CaptureTransform(root));
            }
            if (roots.Count < 2) return EditJson.Err("select at least two different prefab roots");
            try
            {
                for (int i = 0; i < roots.Count; i++)
                    roots[i].transform.position = starts[i] + new Vector3(dx, dy, dz);
                for (int i = 0; i < roots.Count; i++)
                {
                    StopMomentum(roots[i]);
                    roots[i].ForcePositionSync();
                }
            }
            catch (Exception e)
            {
                for (int i = 0; i < roots.Count; i++)
                {
                    try { roots[i].transform.position = starts[i]; roots[i].ForcePositionSync(); } catch { }
                }
                return EditJson.Err("group move failed and was rolled back: " + e.Message);
            }
            List<HistorySnapshot> after = new List<HistorySnapshot>();
            for (int i = 0; i < roots.Count; i++) after.Add(CaptureTransform(roots[i]));
            CommitTransform("move " + roots.Count + " items", before, after);
            return "{\"ok\":true,\"entity\":" + EntityRecord(roots[0], roots[0].transform.position)
                + ",\"note\":\"moved " + roots.Count + " selected entities\"}";
        }

        [Command("rotategroup", "Rotate 2 to 100 roots around the first selected root: edit rotategroup <ids> <ex> <ey> <ez>")]
        public static string RotateGroup(string ids, float ex, float ey, float ez)
        {
            if (!Finite(ex) || !Finite(ey) || !Finite(ez) ||
                Mathf.Abs(ex) > 3600f || Mathf.Abs(ey) > 3600f || Mathf.Abs(ez) > 3600f)
                return EditJson.Err("group rotation angles must be finite and within 3600 degrees");
            List<NetworkEntity> roots;
            string err = ResolveGroup(ids, out roots, 100);
            if (err != null) return EditJson.Err(err);
            if (roots.Count < 2) return EditJson.Err("select at least two different prefab roots");
            List<HistorySnapshot> before = new List<HistorySnapshot>();
            for (int i = 0; i < roots.Count; i++)
            {
                err = VetoMove(roots[i]);
                if (err != null) return EditJson.Err("group rotation refused before moving anything: " + err);
                before.Add(CaptureTransform(roots[i]));
            }
            Vector3 pivot = before[0].Position;
            Quaternion delta = Quaternion.Euler(ex, ey, ez);
            for (int i = 0; i < roots.Count; i++)
            {
                Vector3 target = pivot + delta * (before[i].Position - pivot);
                if (!Finite(target.x) || !Finite(target.y) || !Finite(target.z) ||
                    Mathf.Abs(target.x) > 100000f || Mathf.Abs(target.y) > 100000f || Mathf.Abs(target.z) > 100000f)
                    return EditJson.Err("group rotation would move an item outside the world coordinate limit");
            }
            try
            {
                for (int i = 0; i < roots.Count; i++)
                {
                    Vector3 target = pivot + delta * (before[i].Position - pivot);
                    roots[i].transform.position = target;
                    roots[i].transform.rotation = delta * before[i].Rotation;
                    roots[i].Chunk = LocationChunkHelper.GetBestContaining(target);
                }
                for (int i = 0; i < roots.Count; i++)
                {
                    StopMomentum(roots[i]);
                    roots[i].ForcePositionSync();
                }
            }
            catch (Exception e)
            {
                bool rollbackFailed = false;
                for (int i = roots.Count - 1; i >= 0; i--)
                    try
                    {
                        roots[i].transform.position = before[i].Position;
                        roots[i].transform.rotation = before[i].Rotation;
                        roots[i].Chunk = LocationChunkHelper.GetBestContaining(before[i].Position);
                        roots[i].ForcePositionSync();
                    }
                    catch { rollbackFailed = true; }
                return EditJson.Err("group rotation failed; rollback " +
                    (rollbackFailed ? "was incomplete" : "restored the selection") + ": " + e.Message);
            }
            List<HistorySnapshot> after = new List<HistorySnapshot>();
            for (int i = 0; i < roots.Count; i++) after.Add(CaptureTransform(roots[i]));
            CommitTransform("rotate " + roots.Count + " items", before, after);
            return "{\"ok\":true,\"entity\":" + EntityRecord(roots[0], roots[0].transform.position)
                + ",\"note\":\"rotated " + roots.Count + " selected entities\"}";
        }

        [Command("arrangegroup", "Align or space selected roots: edit arrangegroup <ids> <x|y|z> <align|space> <spacing>")]
        public static string ArrangeGroup(string ids, string axis, string mode, float spacing = 1f)
        {
            if (string.IsNullOrEmpty(axis)) return EditJson.Err("choose axis x, y, or z");
            axis = axis.ToLowerInvariant();
            if (axis != "x" && axis != "y" && axis != "z") return EditJson.Err("axis must be x, y, or z");
            if (string.IsNullOrEmpty(mode)) return EditJson.Err("choose arrange mode align or space");
            mode = mode.ToLowerInvariant();
            if (mode != "align" && mode != "space") return EditJson.Err("mode must be align or space");
            if (!Finite(spacing) || spacing < 0.001f || spacing > 100f)
                return EditJson.Err("spacing must be between 0.001 and 100 metres");

            List<NetworkEntity> roots;
            string err = ResolveGroup(ids, out roots, MaxHistoryGroup);
            if (err != null) return EditJson.Err(err);
            if (roots.Count < 2) return EditJson.Err("select at least two different prefab roots");

            List<HistorySnapshot> before = new List<HistorySnapshot>();
            List<Vector3> starts = new List<Vector3>();
            for (int i = 0; i < roots.Count; i++)
            {
                err = VetoMove(roots[i]);
                if (err != null) return EditJson.Err("arrange stopped before moving anything: " + err);
                starts.Add(roots[i].transform.position);
                before.Add(CaptureTransform(roots[i]));
            }

            float[] targets = new float[roots.Count];
            if (mode == "align")
            {
                float anchor = AxisValue(starts[0], axis);
                for (int i = 0; i < roots.Count; i++) targets[i] = anchor;
            }
            else
            {
                List<int> order = new List<int>();
                for (int i = 0; i < roots.Count; i++) order.Add(i);
                order.Sort(delegate(int a, int b)
                {
                    int compare = AxisValue(starts[a], axis).CompareTo(AxisValue(starts[b], axis));
                    if (compare != 0) return compare;
                    return roots[a].Identifier.CompareTo(roots[b].Identifier);
                });
                float first = AxisValue(starts[order[0]], axis);
                for (int i = 0; i < order.Count; i++) targets[order[i]] = first + spacing * i;
            }
            for (int i = 0; i < roots.Count; i++)
            {
                if (!Finite(targets[i]) || Mathf.Abs(targets[i]) > 100000f)
                    return EditJson.Err("arrange would move an item outside the world coordinate limit");
            }

            try
            {
                for (int i = 0; i < roots.Count; i++)
                {
                    Vector3 target = SetAxis(starts[i], axis, targets[i]);
                    roots[i].transform.position = target;
                    roots[i].Chunk = LocationChunkHelper.GetBestContaining(target);
                }
                for (int i = 0; i < roots.Count; i++)
                {
                    StopMomentum(roots[i]);
                    roots[i].ForcePositionSync();
                }
            }
            catch (Exception e)
            {
                bool rollbackFailed = false;
                for (int i = roots.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        roots[i].transform.position = starts[i];
                        roots[i].Chunk = LocationChunkHelper.GetBestContaining(starts[i]);
                        roots[i].ForcePositionSync();
                    }
                    catch { rollbackFailed = true; }
                }
                return EditJson.Err("arrange failed; rollback " + (rollbackFailed ? "was incomplete" : "restored the selection") + ": " + e.Message);
            }

            List<HistorySnapshot> after = new List<HistorySnapshot>();
            List<string> records = new List<string>();
            for (int i = 0; i < roots.Count; i++)
            {
                after.Add(CaptureTransform(roots[i]));
                records.Add(EntityRecord(roots[i], roots[i].transform.position));
            }
            string label = mode == "align" ? "align " + roots.Count + " items on " + axis.ToUpperInvariant()
                : "space " + roots.Count + " items on " + axis.ToUpperInvariant() + " at " + EditJson.F(spacing) + " m";
            CommitTransform(label, before, after);
            return "{\"ok\":true,\"count\":" + roots.Count + ",\"entities\":["
                + string.Join(",", records.ToArray()) + "],\"note\":\"" + EditJson.Esc(label) + "\"}";
        }

        static float AxisValue(Vector3 value, string axis)
        {
            return axis == "x" ? value.x : (axis == "y" ? value.y : value.z);
        }

        static Vector3 SetAxis(Vector3 value, string axis, float coordinate)
        {
            if (axis == "x") value.x = coordinate;
            else if (axis == "y") value.y = coordinate;
            else value.z = coordinate;
            return value;
        }

        [Command("rotate", "Set an entity's euler rotation (acts on the prefab root)")]
        public static string Rotate(uint id, float ex, float ey, float ez)
        {
            NetworkEntity root;
            string err = ResolveRoot(id, out root);
            if (err == null) err = VetoMove(root);
            if (err != null) return EditJson.Err(err);
            HistorySnapshot before = CaptureTransform(root);
            try
            {
                root.transform.eulerAngles = new Vector3(ex, ey, ez);
                StopMomentum(root);
                root.ForcePositionSync();
            }
            catch (Exception e) { return EditJson.Err("rotate: " + e.Message); }
            CommitTransform("rotate", new List<HistorySnapshot> { before }, new List<HistorySnapshot> { CaptureTransform(root) });
            return Placed(root);
        }

        [Command("scale", "Uniformly scale an entity while preserving its save string: edit scale <id> <factor>")]
        public static string Scale(uint id, float factor)
        {
            if (float.IsNaN(factor) || float.IsInfinity(factor) || factor < 0.5f || factor > 2f)
                return EditJson.Err("scale factor must be between 0.5 and 2");
            NetworkEntity root;
            string err = ResolveRoot(id, out root);
            if (err == null) err = VetoMove(root);
            if (err != null) return EditJson.Err(err);
            string source;
            try { source = SavedDynamicObject.GetSaveOf(root).StringVersion(); }
            catch (Exception e) { return EditJson.Err("scale: could not capture prefab save string: " + e.Message); }
            string scaled;
            err = ScaleSaveString(source, factor, out scaled);
            if (err != null) return EditJson.Err(err);
            return ReplaceEntity(root, scaled, "scale");
        }

        // Deliberately two-step: without the literal word `confirm` it only
        // answers WHAT would be deleted (blueprint-place idiom). Id-targeted,
        // never area. Mounted parts refuse so a wall's fittings can't be eaten
        // by mistake.
        [Command("delete", "Delete an entity - two-step: edit delete <id>, then edit delete <id> confirm")]
        public static string Delete(uint id, string confirm = null)
        {
            NetworkEntity root;
            string err = ResolveRoot(id, out root);
            if (err == null) err = VetoMove(root);
            if (err != null) return EditJson.Err(err);
            string record = EntityRecord(root, root.transform.position);
            HistorySnapshot before;
            err = CaptureSerialized(root, out before);
            if (err != null) return EditJson.Err("delete refused because it could not be made undoable: " + err);
            List<HistorySnapshot> after = new List<HistorySnapshot> { AbsentSnapshot(before.Id) };
            err = ValidateHistorySize(new List<HistorySnapshot> { before }, after);
            if (err != null) return EditJson.Err("delete refused because its undo snapshot is too large: " + err);
            if (confirm != "confirm")
                return "{\"ok\":false,\"needsConfirm\":true,\"count\":1,\"entity\":" + record
                    + ",\"err\":\"add confirm to delete " + EditJson.Esc(root.Prefab != null ? root.Prefab.name : root.OriginalName)
                    + " #" + root.Identifier + "\"}";
            try
            {
                DestroyEntity(root);
            }
            catch (Exception e) { return EditJson.Err("delete: " + e.Message); }
            CommitHistory("delete " + before.Id, new List<HistorySnapshot> { before }, after);
            return "{\"ok\":true,\"deleted\":" + record + "}";
        }

        [Command("deletegroup", "Two-step delete of 1 to 300 selected roots: edit deletegroup <ids> [confirm]")]
        public static string DeleteGroup(string ids, string confirm = null)
        {
            List<NetworkEntity> roots;
            string err = ResolveGroup(ids, out roots, MaxHistoryGroup);
            if (err != null) return EditJson.Err(err);
            List<HistorySnapshot> before = new List<HistorySnapshot>();
            for (int i = 0; i < roots.Count; i++)
            {
                err = VetoMove(roots[i]);
                if (err != null) return EditJson.Err("delete stopped before changing anything: " + err);
                HistorySnapshot snapshot;
                err = CaptureSerialized(roots[i], out snapshot);
                if (err != null) return EditJson.Err("delete stopped before changing anything: " + err);
                before.Add(snapshot);
            }
            List<HistorySnapshot> after = new List<HistorySnapshot>();
            for (int i = 0; i < before.Count; i++) after.Add(AbsentSnapshot(before[i].Id));
            err = ValidateHistorySize(before, after);
            if (err != null) return EditJson.Err("delete stopped before changing anything: " + err);
            if (confirm != "confirm")
                return "{\"ok\":false,\"needsConfirm\":true,\"count\":" + roots.Count
                    + ",\"note\":\"review the selected count, then confirm\"}";

            int destroyed = 0;
            try
            {
                for (; destroyed < roots.Count; destroyed++) DestroyEntity(roots[destroyed]);
            }
            catch (Exception e)
            {
                // Restore the already removed roots so a partial bulk delete is not silently accepted.
                for (int i = destroyed - 1; i >= 0; i--)
                {
                    try { RestoreSerialized(before[i]); } catch { }
                }
                return EditJson.Err("bulk delete failed and rollback was attempted: " + e.Message);
            }
            CommitHistory("delete " + roots.Count + " items", before, after);
            return "{\"ok\":true,\"count\":" + roots.Count + ",\"note\":\"deleted " + roots.Count + " selected items\"}";
        }

        [Command("ground", "Snap an entity down onto the ground")]
        public static string Ground(uint id)
        {
            NetworkEntity root;
            string err = ResolveRoot(id, out root);
            if (err == null) err = VetoMove(root);
            if (err != null) return EditJson.Err(err);
            HistorySnapshot before = CaptureTransform(root);
            try
            {
                RaycastHit hit;
                if (!Physics.Raycast(root.transform.position, Vector3.down, out hit, 1000f))
                    return EditJson.Err("no ground under entity " + root.Identifier);
                root.transform.position = hit.point;
                StopMomentum(root);
                root.ForcePositionSync();
            }
            catch (Exception e) { return EditJson.Err("ground: " + e.Message); }
            CommitTransform("ground", new List<HistorySnapshot> { before }, new List<HistorySnapshot> { CaptureTransform(root) });
            return Placed(root);
        }

        // The in-game editor's paste half: respawn one exported save string at a
        // point. Same machinery as blueprint place (SpawnFromString + reposition +
        // chunk), but one entity, one shot, and the answer carries the new id so
        // the client can pick it straight up. Save strings are a single console
        // token (digits/commas/pipe - the panel validates before piping).
        [Command("paste", "Spawn a save string at a point: edit paste <x> <y> <z> <string>")]
        public static string Paste(float x, float y, float z, string data)
        {
            if (string.IsNullOrEmpty(data)) return EditJson.Err("no save string given");
            if (data.Length > 200000) return EditJson.Err("save string too large");
            NetworkEntity spawned;
            try
            {
                spawned = SerializedSavedDynamicObject.SpawnFromString(data, SaveMode.Base);
            }
            catch (Exception e) { return EditJson.Err("paste: " + e.Message); }
            if (spawned == null) return EditJson.Err("paste: SpawnFromString returned null (bad string?)");
            try
            {
                NetworkEntity root = spawned.PrefabRoot != null ? spawned.PrefabRoot : spawned;
                Vector3 pos = new Vector3(x, y, z);
                root.transform.position = pos;
                root.Chunk = LocationChunkHelper.GetBestContaining(pos);
                StopMomentum(root);
                root.ForcePositionSync();
                HistorySnapshot after;
                string snapshotErr = CaptureSerialized(root, out after);
                List<HistorySnapshot> before = new List<HistorySnapshot> { AbsentSnapshot(root.Identifier) };
                if (snapshotErr != null || ValidateHistorySize(before,
                    new List<HistorySnapshot> { after }) != null)
                {
                    try { DestroyEntity(root); } catch { }
                    return EditJson.Err("paste refused because an undo snapshot could not be stored"
                        + (snapshotErr != null ? ": " + snapshotErr : " (snapshot too large)"));
                }
                CommitHistory("paste " + (root.Prefab != null ? root.Prefab.name : root.OriginalName),
                    before, new List<HistorySnapshot> { after });
                return Placed(root);
            }
            catch (Exception e) { return EditJson.Err("paste: spawned but placing failed: " + e.Message); }
        }

        // In-place editing, the lean half: swap an entity for an edited save
        // string at its exact spot (position AND rotation). The workflow it
        // serves: Copy string -> edit in the workbench -> Replace. The
        // replacement spawns FIRST - a bad string never costs the original -
        // and the old entity goes only once the new one stands. Same vetoes as
        // delete (players, mounted, designated boxes). Deep component mutation
        // without a respawn (LiquidContainer first) stays the next step.
        [Command("replace", "Replace an entity with a save string at its spot: edit replace <id> <string>")]
        public static string Replace(uint id, string data)
        {
            if (string.IsNullOrEmpty(data)) return EditJson.Err("no save string given");
            if (data.Length > 200000) return EditJson.Err("save string too large");
            NetworkEntity old;
            string err = ResolveRoot(id, out old);
            if (err == null) err = VetoMove(old);
            if (err != null) return EditJson.Err(err);
            return ReplaceEntity(old, data, "replace");
        }

        [Command("duplicategroup", "Duplicate 1 to 300 roots by an offset: edit duplicategroup <ids> <dx> <dy> <dz>")]
        public static string DuplicateGroup(string ids, float dx, float dy, float dz)
        {
            if (!Finite(dx) || !Finite(dy) || !Finite(dz)) return EditJson.Err("duplicate offset must be finite");
            List<NetworkEntity> roots;
            string err = ResolveGroup(ids, out roots, MaxHistoryGroup);
            if (err != null) return EditJson.Err(err);

            List<HistorySnapshot> sources = new List<HistorySnapshot>();
            for (int i = 0; i < roots.Count; i++)
            {
                err = VetoMove(roots[i]);
                if (err != null) return EditJson.Err("duplicate stopped before spawning anything: " + err);
                HistorySnapshot snapshot;
                err = CaptureSerialized(roots[i], out snapshot);
                if (err != null) return EditJson.Err("duplicate stopped before spawning anything: " + err);
                sources.Add(snapshot);
            }

            List<HistorySnapshot> plannedBefore = new List<HistorySnapshot>();
            List<HistorySnapshot> plannedAfter = new List<HistorySnapshot>();
            for (int i = 0; i < sources.Count; i++)
            {
                plannedBefore.Add(AbsentSnapshot(0));
                HistorySnapshot planned = new HistorySnapshot();
                planned.Exists = true;
                planned.SaveString = sources[i].SaveString;
                plannedAfter.Add(planned);
            }
            err = ValidateHistorySize(plannedBefore, plannedAfter);
            if (err != null) return EditJson.Err("duplicate refused because its undo snapshot is too large: " + err);

            List<NetworkEntity> copies = new List<NetworkEntity>();
            try
            {
                Vector3 offset = new Vector3(dx, dy, dz);
                for (int i = 0; i < sources.Count; i++)
                {
                    HistorySnapshot source = sources[i];
                    NetworkEntity spawned = SerializedSavedDynamicObject.SpawnFromString(source.SaveString, SaveMode.Base);
                    if (spawned == null) throw new InvalidOperationException("SpawnFromString returned null for " + source.Id);
                    NetworkEntity root = spawned.PrefabRoot != null ? spawned.PrefabRoot : spawned;
                    Vector3 pos = source.Position + offset;
                    root.transform.position = pos;
                    root.transform.rotation = source.Rotation;
                    root.Chunk = LocationChunkHelper.GetBestContaining(pos);
                    StopMomentum(root);
                    root.ForcePositionSync();
                    copies.Add(root);
                }
            }
            catch (Exception e)
            {
                for (int i = copies.Count - 1; i >= 0; i--)
                    try { DestroyEntity(copies[i]); } catch { }
                return EditJson.Err("duplicate failed; spawned copies were removed where possible: " + e.Message);
            }

            List<HistorySnapshot> before = new List<HistorySnapshot>();
            List<HistorySnapshot> after = new List<HistorySnapshot>();
            List<string> records = new List<string>();
            for (int i = 0; i < copies.Count; i++)
            {
                NetworkEntity copy = copies[i];
                before.Add(AbsentSnapshot(copy.Identifier));
                HistorySnapshot snapshot = new HistorySnapshot();
                snapshot.Id = copy.Identifier;
                snapshot.Exists = true;
                snapshot.SaveString = sources[i].SaveString;
                snapshot.Position = copy.transform.position;
                snapshot.Rotation = copy.transform.rotation;
                after.Add(snapshot);
                records.Add(EntityRecord(copy, copy.transform.position));
            }
            CommitHistory("duplicate " + copies.Count + " items", before, after);
            return "{\"ok\":true,\"count\":" + copies.Count + ",\"entities\":["
                + string.Join(",", records.ToArray()) + "],\"note\":\"duplicated " + copies.Count + " selected items\"}";
        }

        // Spawn-with-preview's server half: the client picked a prefab from the
        // panel catalog and aimed a ghost; this drops the real thing at that point.
        // The prefab arrives as the catalog HASH (one console token - names can
        // contain spaces); a bare name still works for hand-driven console use.
        // Same spawn idiom as the craft engine's output spawner (live-proven):
        // PrepareSpawnSetups once, GetPrefab, SpawnHelper, chunk, sync.
        [Command("spawnat", "Spawn a prefab at a point: edit spawnat <x> <y> <z> <prefab-hash-or-name>")]
        public static string SpawnAt(float x, float y, float z, string prefab)
        {
            if (string.IsNullOrEmpty(prefab)) return EditJson.Err("no prefab given");
            try
            {
                if (!_spawnSetupsPrepared)
                {
                    try { PrefabManager.PrepareSpawnSetups(); } catch { }
                    _spawnSetupsPrepared = true;
                }
                NetworkPrefab np = null;
                uint hash;
                if (uint.TryParse(prefab, out hash)) np = PrefabManager.GetPrefab(hash);
                if (np == null) np = PrefabManager.GetPrefab(prefab);
                // GetPrefab(string) compares against LOWERCASED prefab names but
                // never lowers the input - hand-typed names need these:
                if (np == null) np = PrefabManager.GetPrefab(prefab.ToLowerInvariant());
                if (np == null) np = PrefabManager.GetPrefab(prefab.ToLowerInvariant().Replace(" ", ""));
                if (np == null) return EditJson.Err("no prefab " + prefab);
                try
                {
                    if (!np.CanBeSpawnedThroughCommand)
                        return EditJson.Err("prefab " + np.name + " is not spawnable");
                }
                catch { }
                Vector3 pos = new Vector3(x, y, z);
                NetworkEntity spawned = SpawnHelper.Spawn(np, SpawnData.Default, null, pos, Quaternion.identity);
                if (spawned == null) return EditJson.Err("spawnat: SpawnHelper returned null");
                NetworkEntity root = spawned.PrefabRoot != null ? spawned.PrefabRoot : spawned;
                root.Chunk = LocationChunkHelper.GetBestContaining(pos);
                StopMomentum(root);
                root.ForcePositionSync();
                return Placed(root);
            }
            catch (Exception e) { return EditJson.Err("spawnat: " + e.Message); }
        }

        // ---------------- undo/redo and safe prefab snapshots ----------------

        static string ReplaceEntity(NetworkEntity old, string data, string label)
        {
            HistorySnapshot before;
            string err = CaptureSerialized(old, out before);
            if (err != null) return EditJson.Err(label + " refused because undo could not be recorded: " + err);
            HistorySnapshot after = new HistorySnapshot();
            after.Exists = true;
            after.SaveString = data;
            err = ValidateHistorySize(new List<HistorySnapshot> { before }, new List<HistorySnapshot> { after });
            if (err != null) return EditJson.Err(label + " refused because its undo snapshot is too large: " + err);
            Vector3 pos = old.transform.position;
            Quaternion rot = old.transform.rotation;
            string oldRecord = EntityRecord(old, pos);
            NetworkEntity spawned;
            try { spawned = SerializedSavedDynamicObject.SpawnFromString(data, SaveMode.Base); }
            catch (Exception e) { return EditJson.Err(label + ": " + e.Message + " - original untouched"); }
            if (spawned == null) return EditJson.Err(label + ": SpawnFromString returned null - original untouched");
            NetworkEntity root = spawned.PrefabRoot != null ? spawned.PrefabRoot : spawned;
            try
            {
                root.transform.position = pos;
                root.transform.rotation = rot;
                root.Chunk = LocationChunkHelper.GetBestContaining(pos);
                StopMomentum(root);
                root.ForcePositionSync();
            }
            catch (Exception e)
            {
                try { DestroyEntity(root); } catch { }
                return EditJson.Err(label + ": replacement spawned but placement failed: " + e.Message);
            }
            try { DestroyEntity(old); }
            catch (Exception e)
            {
                try { DestroyEntity(root); } catch { }
                return EditJson.Err(label + ": could not remove original; replacement was removed where possible: " + e.Message);
            }
            after.Id = root.Identifier;
            after.Position = pos;
            after.Rotation = rot;
            CommitHistory(label, new List<HistorySnapshot> { before }, new List<HistorySnapshot> { after });
            return "{\"ok\":true,\"entity\":" + EntityRecord(root, root.transform.position)
                + ",\"replaced\":" + oldRecord + ",\"note\":\"" + EditJson.Esc(label) + " complete\"}";
        }

        static string ScaleSaveString(string source, float factor, out string result)
        {
            result = null;
            if (string.IsNullOrEmpty(source) || source.Length > 200000 || source.IndexOf('|') < 0)
                return "prefab has no valid save string";
            int pipe = source.IndexOf('|');
            string[] words = source.Substring(0, pipe).Split(',');
            if (words.Length < 11) return "save string has no scale word at index 10";
            uint packed;
            if (!uint.TryParse(words[10], out packed)) return "save string scale word is invalid";
            float current = BitConverter.ToSingle(BitConverter.GetBytes(packed), 0);
            if (float.IsNaN(current) || float.IsInfinity(current) || current <= 0f)
                return "prefab has an invalid scale";
            float next = Mathf.Clamp(current * factor, 0.05f, 20f);
            if (Mathf.Abs(next - current) < 0.000001f) return "prefab is already at the scale limit";
            words[10] = BitConverter.ToUInt32(BitConverter.GetBytes(next), 0).ToString();
            result = string.Join(",", words) + source.Substring(pipe);
            return null;
        }

        static string CaptureSerialized(NetworkEntity root, out HistorySnapshot snapshot)
        {
            snapshot = CaptureTransform(root);
            try
            {
                snapshot.SaveString = SavedDynamicObject.GetSaveOf(root).StringVersion();
                if (string.IsNullOrEmpty(snapshot.SaveString)) return "save string capture returned no data for #" + snapshot.Id;
                return null;
            }
            catch (Exception e) { return "save string capture failed for #" + snapshot.Id + ": " + e.Message; }
        }

        static HistorySnapshot CaptureTransform(NetworkEntity root)
        {
            HistorySnapshot s = new HistorySnapshot();
            s.Id = root.Identifier;
            s.Exists = true;
            s.Position = root.transform.position;
            s.Rotation = root.transform.rotation;
            return s;
        }

        static HistorySnapshot AbsentSnapshot(uint id)
        {
            HistorySnapshot s = new HistorySnapshot();
            s.Id = id;
            s.Exists = false;
            return s;
        }

        static void CommitTransform(string label, List<HistorySnapshot> before, List<HistorySnapshot> after)
        {
            CommitHistory(label, before, after);
        }

        static void CommitHistory(string label, List<HistorySnapshot> before, List<HistorySnapshot> after)
        {
            HistoryEntry entry = new HistoryEntry();
            entry.Label = label;
            entry.Before.AddRange(before);
            entry.After.AddRange(after);
            entry.Bytes = EstimateHistoryBytes(before, after, label);
            for (int i = 0; i < _redo.Count; i++) _historyBytes -= _redo[i].Bytes;
            _redo.Clear();
            _undo.Add(entry);
            _historyBytes += entry.Bytes;
            TrimHistory();
        }

        static void TrimHistory()
        {
            while (_undo.Count + _redo.Count > MaxHistoryEntries || _historyBytes > MaxHistoryBytes)
            {
                if (_undo.Count > 1)
                {
                    _historyBytes -= _undo[0].Bytes;
                    _undo.RemoveAt(0);
                }
                else if (_redo.Count > 0)
                {
                    _historyBytes -= _redo[0].Bytes;
                    _redo.RemoveAt(0);
                }
                else break;
            }
        }

        static string ValidateHistorySize(List<HistorySnapshot> before, List<HistorySnapshot> after)
        {
            long bytes = EstimateHistoryBytes(before, after, "");
            if (bytes > MaxHistoryEntryBytes)
                return "one action would need about " + (bytes / (1024 * 1024)) + " MiB; the per-action limit is 8 MiB";
            return null;
        }

        static int EstimateHistoryBytes(List<HistorySnapshot> before, List<HistorySnapshot> after, string label)
        {
            long bytes = 128 + (label == null ? 0 : label.Length * 2);
            if (before != null)
                for (int i = 0; i < before.Count; i++) bytes += EstimateSnapshotBytes(before[i]);
            if (after != null)
                for (int i = 0; i < after.Count; i++) bytes += EstimateSnapshotBytes(after[i]);
            return bytes > int.MaxValue ? int.MaxValue : (int)bytes;
        }

        static int EstimateSnapshotBytes(HistorySnapshot snapshot)
        {
            if (snapshot == null) return 32;
            long bytes = 96;
            if (snapshot.SaveString != null) bytes += (long)snapshot.SaveString.Length * 2;
            return bytes > int.MaxValue ? int.MaxValue : (int)bytes;
        }

        static string HistoryState()
        {
            string undoLabel = _undo.Count == 0 ? "" : _undo[_undo.Count - 1].Label;
            string redoLabel = _redo.Count == 0 ? "" : _redo[_redo.Count - 1].Label;
            return "{\"ok\":true,\"undoCount\":" + _undo.Count + ",\"redoCount\":" + _redo.Count
                + ",\"undoLabel\":\"" + EditJson.Esc(undoLabel) + "\",\"redoLabel\":\""
                + EditJson.Esc(redoLabel) + "\"}";
        }

        static string HistoryAction(string note, List<HistorySnapshot> current)
        {
            List<string> records = new List<string>();
            for (int i = 0; i < current.Count; i++)
            {
                if (!current[i].Exists) continue;
                NetworkEntity root;
                if (ResolveRoot(current[i].Id, out root) == null)
                    records.Add(EntityRecord(root, root.transform.position));
            }
            return "{\"ok\":true,\"note\":\"" + EditJson.Esc(note) + "\",\"undoCount\":"
                + _undo.Count + ",\"redoCount\":" + _redo.Count + ",\"entities\":["
                + string.Join(",", records.ToArray()) + "]}";
        }

        static string ApplyHistory(HistoryEntry entry, bool applyBefore)
        {
            List<HistorySnapshot> from = applyBefore ? entry.After : entry.Before;
            List<HistorySnapshot> to = applyBefore ? entry.Before : entry.After;
            if (from.Count != to.Count) return "history record is incomplete";

            // Validate every currently live entity before changing any of them.
            for (int i = 0; i < from.Count; i++)
            {
                if (!from[i].Exists) continue;
                NetworkEntity root;
                string err = ResolveRoot(from[i].Id, out root);
                if (err != null) return err;
                err = VetoMove(root);
                if (err != null) return "#" + from[i].Id + " cannot be changed now: " + err;
            }

            int applied = 0;
            for (; applied < from.Count; applied++)
            {
                string err = ApplyHistoryItem(from[applied], to[applied]);
                if (err == null) continue;
                bool rollbackFailed = false;
                for (int i = applied - 1; i >= 0; i--)
                    if (ApplyHistoryItem(to[i], from[i]) != null) rollbackFailed = true;
                return err + (rollbackFailed ? " (some earlier items could not be rolled back)" : " (earlier items rolled back)");
            }
            return null;
        }

        static string ApplyHistoryItem(HistorySnapshot from, HistorySnapshot target)
        {
            NetworkEntity current = null;
            if (from.Exists)
            {
                string err = ResolveRoot(from.Id, out current);
                if (err != null) return err;
            }
            if (!target.Exists)
            {
                try { DestroyEntity(current); return null; }
                catch (Exception e) { return "could not restore deletion state for #" + from.Id + ": " + e.Message; }
            }
            if (!string.IsNullOrEmpty(target.SaveString))
            {
                NetworkEntity restored = null;
                try
                {
                    NetworkEntity spawned = SerializedSavedDynamicObject.SpawnFromString(target.SaveString, SaveMode.Base);
                    if (spawned == null) return "could not restore prefab snapshot for #" + target.Id;
                    restored = spawned.PrefabRoot != null ? spawned.PrefabRoot : spawned;
                    restored.transform.position = target.Position;
                    restored.transform.rotation = target.Rotation;
                    restored.Chunk = LocationChunkHelper.GetBestContaining(target.Position);
                    StopMomentum(restored);
                    restored.ForcePositionSync();
                }
                catch (Exception e)
                {
                    if (restored != null) try { DestroyEntity(restored); } catch { }
                    return "could not restore prefab snapshot for #" + target.Id + ": " + e.Message;
                }
                if (current != null)
                {
                    try { DestroyEntity(current); }
                    catch (Exception e)
                    {
                        try { DestroyEntity(restored); } catch { }
                        return "could not remove current version of #" + from.Id + ": " + e.Message;
                    }
                }
                target.Id = restored.Identifier;
                return null;
            }
            if (current == null) return "history transform no longer has a live entity (#" + target.Id + ")";
            try
            {
                current.transform.position = target.Position;
                current.transform.rotation = target.Rotation;
                current.Chunk = LocationChunkHelper.GetBestContaining(target.Position);
                StopMomentum(current);
                current.ForcePositionSync();
                target.Id = current.Identifier;
                return null;
            }
            catch (Exception e) { return "could not restore transform for #" + target.Id + ": " + e.Message; }
        }

        static string ResolveGroup(string ids, out List<NetworkEntity> roots, int max)
        {
            roots = new List<NetworkEntity>();
            if (string.IsNullOrEmpty(ids)) return "no selected entity ids";
            string[] parts = ids.Split(',');
            if (parts.Length < 1 || parts.Length > max) return "selection must contain between 1 and " + max + " entity ids";
            HashSet<uint> seen = new HashSet<uint>();
            for (int i = 0; i < parts.Length; i++)
            {
                uint id;
                if (!uint.TryParse(parts[i], out id) || id == 0) return "invalid entity id in selection";
                if (!seen.Add(id)) continue;
                NetworkEntity root;
                string err = ResolveRoot(id, out root);
                if (err != null) return err;
                if (!roots.Contains(root)) roots.Add(root);
            }
            if (roots.Count == 0) return "selection contains no distinct entities";
            return null;
        }

        static void DestroyEntity(NetworkEntity root)
        {
            if (root == null) throw new InvalidOperationException("entity reference is missing");
            if (root.Spawner != null) root.Spawner.Destroy(root);
            else root.SceneDestroy();
        }

        static HistorySnapshot RestoreSerialized(HistorySnapshot snapshot)
        {
            NetworkEntity spawned = SerializedSavedDynamicObject.SpawnFromString(snapshot.SaveString, SaveMode.Base);
            if (spawned == null) throw new InvalidOperationException("SpawnFromString returned null for #" + snapshot.Id);
            NetworkEntity root = spawned.PrefabRoot != null ? spawned.PrefabRoot : spawned;
            root.transform.position = snapshot.Position;
            root.transform.rotation = snapshot.Rotation;
            root.Chunk = LocationChunkHelper.GetBestContaining(snapshot.Position);
            StopMomentum(root);
            root.ForcePositionSync();
            snapshot.Id = root.Identifier;
            return snapshot;
        }

        static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        static bool _spawnSetupsPrepared;

        // ---------------------------------------------------------------
        //  Scan internals
        // ---------------------------------------------------------------

        sealed class ScanHit
        {
            public NetworkEntity Entity;
            public float Distance;
        }

        static string ScanAround(Vector3 anchor, float radius, string playerJson)
        {
            if (radius < 1f) radius = 1f;
            if (radius > MaxRadius) radius = MaxRadius;

            List<ScanHit> hits = new List<ScanHit>();
            try
            {
                Dictionary<uint, bool> seen = new Dictionary<uint, bool>();
                foreach (NetworkEntity e in AllEntities())
                {
                    if (e == null || e.IsBeingDestroyed) continue;
                    NetworkEntity root = e.PrefabRoot != null ? e.PrefabRoot : e;
                    if (seen.ContainsKey(root.Identifier)) continue;
                    seen[root.Identifier] = true;
                    if (IsPlayer(root)) continue;
                    Pickup p = root.CommonPickup;
                    if (p != null && (p.IsDocked || p.IsInteractedWith)) continue; // in storage / in a hand
                    float d;
                    try { d = Vector3.Distance(root.transform.position, anchor); }
                    catch { continue; }
                    if (d > radius) continue;
                    ScanHit hit = new ScanHit();
                    hit.Entity = root;
                    hit.Distance = d;
                    hits.Add(hit);
                }
            }
            catch (Exception e) { return EditJson.Err("scan: " + e.Message); }

            hits.Sort(delegate(ScanHit a, ScanHit b) { return a.Distance.CompareTo(b.Distance); });
            bool truncated = hits.Count > MaxEntities;
            int n = truncated ? MaxEntities : hits.Count;

            List<string> records = new List<string>(n);
            for (int i = 0; i < n; i++)
                records.Add(EntityRecord(hits[i].Entity, anchor));

            return "{\"ok\":true"
                + ",\"anchor\":{\"x\":" + EditJson.F(anchor.x) + ",\"y\":" + EditJson.F(anchor.y) + ",\"z\":" + EditJson.F(anchor.z) + "}"
                + ",\"radius\":" + EditJson.F(radius)
                + (playerJson != null ? ",\"player\":" + playerJson : "")
                + ",\"count\":" + n
                + ",\"truncated\":" + (truncated ? "true" : "false")
                + ",\"entities\":[" + string.Join(",", records.ToArray()) + "]}";
        }

        // One entity as a compact JSON record. Distance is measured from anchor.
        static string EntityRecord(NetworkEntity root, Vector3 anchor)
        {
            string name = root.Prefab != null ? root.Prefab.name : root.OriginalName;
            uint hash = 0;
            try { if (root.Prefab != null) hash = root.Prefab.Hash; } catch { }
            Vector3 pos = root.transform.position;
            Vector3 eul = root.transform.eulerAngles;

            List<string> flags = new List<string>();
            Pickup p = root.CommonPickup;
            if (p != null)
            {
                flags.Add("pickup");
                try { if (p.IsEnvironmental) flags.Add("env"); } catch { }
                try { if (p.IsChunkingParentStatic) flags.Add("static"); } catch { }
            }
            else flags.Add("fixture");
            if (IsMounted(root)) flags.Add("mounted");

            List<string> quoted = new List<string>(flags.Count);
            foreach (string f in flags) quoted.Add("\"" + f + "\"");

            return "{\"id\":" + root.Identifier
                + ",\"prefab\":\"" + EditJson.Esc(name) + "\""
                + ",\"hash\":" + hash
                + ",\"x\":" + EditJson.F(pos.x) + ",\"y\":" + EditJson.F(pos.y) + ",\"z\":" + EditJson.F(pos.z)
                + ",\"ex\":" + EditJson.F(eul.x) + ",\"ey\":" + EditJson.F(eul.y) + ",\"ez\":" + EditJson.F(eul.z)
                + ",\"d\":" + EditJson.F(Vector3.Distance(pos, anchor))
                + ",\"flags\":[" + string.Join(",", quoted.ToArray()) + "]}";
        }

        // ---------------------------------------------------------------
        //  Move plumbing
        // ---------------------------------------------------------------

        static string ResolveRoot(uint id, out NetworkEntity root)
        {
            root = null;
            NetworkEntity e = null;
            try { e = NetworkSceneManager.Current.GetEntity(id); } catch { }
            if (e == null) e = FindEntity(id);
            if (e == null || e.IsBeingDestroyed) return "no entity with id " + id + " (chunk unloaded?)";
            root = e.PrefabRoot != null ? e.PrefabRoot : e;
            return null;
        }

        // Position belongs to whoever owns the attachment: docks, hands and
        // mounts all veto. The world itself (static/env fixtures) is fair game -
        // that is what the editor is for.
        static string VetoMove(NetworkEntity root)
        {
            if (IsPlayer(root)) return "that's a player - use `player teleport`";
            Pickup p = root.CommonPickup;
            if (p != null)
            {
                try { if (p.IsDocked) return "entity " + root.Identifier + " is docked in storage - take it out first"; } catch { }
                try { if (p.IsInteractedWith) return "entity " + root.Identifier + " is in someone's hand"; } catch { }
            }
            if (IsMounted(root)) return "entity " + root.Identifier + " is mounted on another item - move the base";
            return null;
        }

        // A teleported dynamic item must not keep its old momentum.
        static void StopMomentum(NetworkEntity root)
        {
            try
            {
                Pickup p = root.CommonPickup;
                if (p == null) return;
                Rigidbody rb = p.Rigidbody;
                if (rb != null && !rb.isKinematic)
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
            }
            catch { }
        }

        // Post-move response.
        static string Placed(NetworkEntity root)
        {
            return "{\"ok\":true,\"entity\":" + EntityRecord(root, root.transform.position) + "}";
        }

        // ---------------------------------------------------------------
        //  Shared predicates / iteration (house style: modules stay standalone)
        // ---------------------------------------------------------------

        static bool IsPlayer(NetworkEntity e)
        {
            try { return e.GetComponent<PlayerController>() != null; }
            catch { return false; }
        }

        // Assembly mounts are Unity Joints on the mounted part -
        // but articulated prefabs carry INTERNAL joints too (a chest's lid hinges onto
        // its own body), and the old any-joint test false-vetoed every one of them
        // (a fresh chest refused both move and delete). Mounted = a joint
        // that anchors OUTSIDE this prefab root: connectedBody under another root,
        // under no NetworkEntity at all, or missing entirely (pinned to the world).
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
            catch { return true; } // can't tell -> don't move it
        }

        static string SafeUsername(Player p)
        {
            try { return p.UserInfo.Username; }
            catch { return p == null ? "" : p.ToString(); }
        }

        static NetworkEntity FindEntity(uint id)
        {
            foreach (NetworkEntity e in AllEntities())
                if (e != null && e.Identifier == id) return e;
            return null;
        }

        // Scene-wide iteration, same as the game's own PrintPrefabsCount:
        // NetworkScene's private entityManager -> EntityManager.IterateEntities().
        // Falls back to the public per-chunk walk if the field ever moves.
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
            return ChunkWalk();
        }

        static IEnumerable<NetworkEntity> ChunkWalk()
        {
            List<NetworkEntity> result = new List<NetworkEntity>();
            foreach (Chunk chunk in new List<Chunk>(Chunk.ChunksByIndex.Values))
            {
                if (chunk == null || chunk.Entities == null) continue;
                foreach (NetworkEntity e in chunk.Entities.Entities) result.Add(e);
            }
            return result;
        }
    }
}
