using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Alta.Chunks;
using HarmonyLib;
using UnityEngine;

namespace PrefabEditor.Modules
{
    // Server-side loading gate for explicit Prefabulator requests only.
    // Uses the unmodified client's built-in FullyLoaded acknowledgement protocol.
    internal static class SafeTeleport
    {
        sealed class Trip
        {
            public Player Player;
            public PlayerController Body;
            public PlayerChunkManager Manager;
            public LocationChunk Target, OriginChunk;
            public Vector3 Destination, Origin;
            public Chunk[] OriginChunks;
            public Task Loading;
            public object LoadToken;
            public object DestinationSync;
            public object PreloadOwner;
            public bool MoveRequested;
            public bool AwaitingArrival;
            public bool Started;
            public bool CancelAfterLoad;
            public float Deadline;
            public string Error;
        }
        sealed class Pin { public int Count; public bool WasForced; }
        static readonly Dictionary<PlayerChunkManager, Trip> trips = new Dictionary<PlayerChunkManager, Trip>();
        static readonly Dictionary<Chunk, Pin> pins = new Dictionary<Chunk, Pin>();
        static readonly FieldInfo loadTask = typeof(Player).GetField("loadStatusTask", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly MethodInfo awaitChunk = AccessTools.Method(typeof(Player), "AwaitRemoteClientToLoadChunk");
        static readonly MethodInfo loadPosition = AccessTools.Method(typeof(PlayerChunkManager), "LoadFromPosition");
        static HarmonyLib.Harmony harmony;
        static bool executing, startingLoad;
        static Action<string> log;

        internal static void Initialize(Action<string> logger)
        {
            log = logger;
            if (loadTask == null || awaitChunk == null || loadPosition == null) throw new Exception("safe teleport requires the game's chunk acknowledgement support");
            harmony = new HarmonyLib.Harmony("prefabeditor.safe-teleport");
            try {
                harmony.Patch(AccessTools.Method(typeof(PlayerController), "ForceTeleportTo"), new HarmonyMethod(typeof(SafeTeleport), "TeleportPrefix"));
                harmony.Patch(AccessTools.Method(typeof(PlayerChunkManager), "LoadChunkAt"), new HarmonyMethod(typeof(SafeTeleport), "ExternalLoadPrefix"));
                harmony.Patch(AccessTools.Method(typeof(PlayerChunkManager), "HandleChunkSync"), new HarmonyMethod(typeof(SafeTeleport), "ChunkSyncPrefix"));
                harmony.Patch(AccessTools.Method(typeof(PlayerChunkManager), "LoadFromPosition"), new HarmonyMethod(typeof(SafeTeleport), "LoadingPositionPrefix"));
                harmony.Patch(AccessTools.Method(typeof(ChunkPlayerManager), "Remove"), new HarmonyMethod(typeof(SafeTeleport), "RemoveSourcePrefix"));
            } catch { harmony.UnpatchSelf(); throw; }
        }

        static bool TeleportPrefix(PlayerController __instance, Vector3 __0)
        {
            // Another mod owns this movement. Never wrap it in our preload or
            // leave our old position pin overriding its chunk request.
            if (!executing && __instance != null && __instance.NetworkPlayer != null)
                Abandon(__instance.NetworkPlayer.ChunkManager);
            return true;
        }

        internal static void Request(PlayerController player, Vector3 destination)
        {
            if (Begin(player,destination,null)) player.ForceTeleportTo(destination);
        }
        static void Abandon(PlayerChunkManager manager)
        {
            Trip trip;
            if (manager == null || !trips.TryGetValue(manager,out trip)) return;
            trips.Remove(manager);
            Release(trip);
            if (log != null) log("Released Prefabulator preload for another teleport transition.");
        }
        static void ExternalLoadPrefix(PlayerChunkManager __instance)
        {
            if (!startingLoad && !executing) Abandon(__instance);
        }

        internal static bool Preload(PlayerController player, Vector3 destination, object owner)
        {
            if (owner != null) Begin(player, destination, owner);
            Trip trip;
            return player != null && player.NetworkPlayer != null && player.NetworkPlayer.ChunkManager != null &&
                trips.TryGetValue(player.NetworkPlayer.ChunkManager, out trip) && ReferenceEquals(trip.PreloadOwner, owner);
        }

        internal static void CancelPreload(PlayerController player, object owner)
        {
            Trip trip;
            if (player != null && player.NetworkPlayer != null && player.NetworkPlayer.ChunkManager != null &&
                trips.TryGetValue(player.NetworkPlayer.ChunkManager, out trip) &&
                ReferenceEquals(trip.PreloadOwner, owner)) {
                // The successful move itself generates an exit event. It must
                // not cancel the transfer before its position reaches the server.
                if (trip.AwaitingArrival) return;
                if (trip.Started && trip.Loading != null && !trip.Loading.IsCompleted) {
                    // Unloading now can prevent the native acknowledgement forever.
                    // Finish this preload quietly, then restore the departure chunks.
                    trip.CancelAfterLoad = true;
                    return;
                }
                Cancel(trip, "player stepped off the platform", false);
            }
        }

        static bool Begin(PlayerController __instance, Vector3 __0, object owner)
        {
            if (!NetworkSceneManager.IsServer || executing) return true;
            Player player = __instance.NetworkPlayer;
            if (player == null || player.ChunkManager == null) throw new Exception("player is not ready for safe teleport");
            Trip pending;
            if (trips.TryGetValue(player.ChunkManager, out pending)) {
                if ((__0 - pending.Destination).sqrMagnitude < .0001f) {
                    if (owner == null) pending.MoveRequested = true;
                    pending.CancelAfterLoad = false;
                }
                return false; // Reuse the contact preload, never issue a second acknowledgement request.
            }
            if (!Finite(__0.x) || !Finite(__0.y) || !Finite(__0.z)) throw new Exception("invalid teleport coordinates");
            LocationChunkHelper.EnsureChunkLookupIsSetup();
            LocationChunk target = LocationChunkHelper.GetBestContaining(__0);
            if (target == null) throw new Exception("no map chunk contains the teleport destination");
            // Keep-alive nudges do not need a new chunk or interrupt a native transition.
            if (owner == null && (__0 - __instance.transform.position).sqrMagnitude < 1f && target == player.ChunkManager.Chunk && target.IsFullyLoaded) return true;

            Trip trip = new Trip { Player = player, Body = __instance, Manager = player.ChunkManager,
                Target = target, Destination = __0, Origin = __instance.transform.position,
                OriginChunk = player.ChunkManager.Chunk, OriginChunks = new Chunk[0],
                Deadline = Time.unscaledTime + 30f, PreloadOwner = owner, MoveRequested = owner == null };
            trips.Add(trip.Manager, trip);
            try {
                TryStartLoading(trip);
            } catch {
                trips.Remove(trip.Manager);
                RestoreOrigin(trip);
                Release(trip);
                throw;
            }
            return false;
        }

        static bool TryStartLoading(Trip trip)
        {
            // Native acknowledgements have no request ID. Let the previous
            // housing/native/canceled request finish before issuing a new one.
            var existing = loadTask.GetValue(trip.Player) as TaskCompletionSource<bool>;
            if (existing != null && !existing.Task.IsCompleted) return false;
            trip.Origin = trip.Body.transform.position;
            trip.OriginChunk = trip.Manager.Chunk;
            trip.OriginChunks = new List<Chunk>(trip.Player.LoadedChunks).ToArray();
            trip.Started = true;
            foreach (Chunk chunk in trip.OriginChunks) PinChunk(chunk);
            try {
                startingLoad = true;
                trip.Loading = (Task)awaitChunk.Invoke(trip.Player,new object[] {trip.Target,trip.Destination});
            } finally { startingLoad = false; }
            if (trip.Body.Entity != null) trip.Body.Entity.Chunk = trip.OriginChunk;
            trip.LoadToken = loadTask.GetValue(trip.Player);
            if (log != null) log("Loading destination before teleport for " + trip.Player.Identifier + ".");
            return true;
        }

        static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
        static void PinChunk(Chunk chunk)
        {
            if (chunk == null) return;
            Pin pin;
            if (!pins.TryGetValue(chunk, out pin)) {
                pin = new Pin { WasForced = chunk.IsForceLoaded };
                pins.Add(chunk, pin);
                chunk.IsForceLoaded = true;
            }
            pin.Count++;
        }
        static void Release(Trip trip)
        {
            foreach (Chunk chunk in trip.OriginChunks) {
                Pin pin;
                if (chunk == null || !pins.TryGetValue(chunk, out pin)) continue;
                if (--pin.Count > 0) continue;
                pins.Remove(chunk);
                chunk.IsForceLoaded = pin.WasForced;
            }
        }

        static void LoadingPositionPrefix(PlayerChunkManager __instance, ref Vector3 __0)
        {
            Trip trip;
            if (trips.TryGetValue(__instance, out trip) && trip.Started) __0 = trip.Destination;
        }

        static bool RemoveSourcePrefix(ChunkPlayerManager __instance, Player __0, ref bool __result)
        {
            Trip trip;
            if (__0 != null && __0.ChunkManager != null && trips.TryGetValue(__0.ChunkManager, out trip) &&
                !__0.IsBeingDestroyed && Array.IndexOf(trip.OriginChunks, __instance.Chunk) >= 0) {
                __result = false;
                return false;
            }
            return true;
        }

        static void ChunkSyncPrefix(PlayerChunkManager __instance, Alta.Serialization.Stream __1, object[] __args)
        {
            Trip trip;
            if (!NetworkSceneManager.IsServer || !__1.IsWriting || !trips.TryGetValue(__instance, out trip) || !trip.Started) return;
            // Native preload normally removes the departure chunks. Retain them on
            // the client as well as on the server until the teleport has committed.
            // ChunksToSync is internal in r8. Work with a boxed copy of the actual
            // argument so Harmony writes it back without referencing that type.
            object packet = __args[2];
            FieldInfo loaded = packet.GetType().GetField("loadedChunks");
            FieldInfo current = packet.GetType().GetField("currentChunk");
            if (!trip.AwaitingArrival)
                trip.DestinationSync = System.Runtime.CompilerServices.RuntimeHelpers.GetObjectValue(packet);
            HashSet<Chunk> combined = new HashSet<Chunk>((Chunk[])loaded.GetValue(packet) ?? new Chunk[0]);
            foreach (Chunk chunk in trip.OriginChunks) if (chunk != null) combined.Add(chunk);
            if (combined.Count > 127) {
                trip.Error = "too many chunks to preload safely";
                loaded.SetValue(packet, trip.OriginChunks);
            } else {
                loaded.SetValue(packet, new List<Chunk>(combined).ToArray());
            }
            current.SetValue(packet, trip.AwaitingArrival ? trip.Target : trip.OriginChunk);
            __args[2] = packet;
        }

        internal static void Tick()
        {
            foreach (Trip trip in new List<Trip>(trips.Values)) {
                try { TickTrip(trip); }
                catch (Exception e) { Cancel(trip, "loading failed: " + e.Message); }
            }
        }
        static void TickTrip(Trip trip)
        {
            if (trip.Player == null || trip.Body == null || trip.Player.IsBeingDestroyed || trip.Body != trip.Player.PlayerController) {
                trips.Remove(trip.Manager); Release(trip); return;
            }
            if (!trip.Body.IsAlive) { Cancel(trip, "player is no longer alive"); return; }
            if (!trip.Started) {
                if (Time.unscaledTime >= trip.Deadline) { Cancel(trip,"previous chunk transition did not finish",trip.MoveRequested); return; }
                if (!TryStartLoading(trip)) return;
            }
            if (!ReferenceEquals(loadTask.GetValue(trip.Player), trip.LoadToken)) {
                // Another native transition owns the client now; don't undo its chunk state.
                trips.Remove(trip.Manager); Release(trip); return;
            }
            if (trip.AwaitingArrival) {
                // ForceTeleportTo sends a request to remote clients; it does not
                // update their server transform synchronously. Keep both sides
                // loaded until the client's new position has actually arrived.
                if ((trip.Body.transform.position - trip.Destination).sqrMagnitude <= 1f) {
                    trips.Remove(trip.Manager);
                    try { loadPosition.Invoke(trip.Manager, new object[] { trip.Destination }); }
                    finally { Release(trip); }
                    if (log != null) log("Teleport arrival confirmed for " + trip.Player.Identifier + ".");
                } else if (Time.unscaledTime >= trip.Deadline) {
                    Cancel(trip, "client did not confirm teleport arrival within 10 seconds");
                }
                return;
            }
            if (trip.CancelAfterLoad && trip.Loading.IsCompleted) { Cancel(trip,"player stepped off the platform",false); return; }
            if (trip.Error != null) { Cancel(trip, trip.Error); return; }
            if (Time.unscaledTime >= trip.Deadline) { Cancel(trip, "destination did not finish loading with solid ground within 30 seconds", trip.MoveRequested); return; }
            if (trip.Loading.IsFaulted || trip.Loading.IsCanceled) { Cancel(trip, "client chunk loading failed"); return; }
            // Contact loads the destination while the native platform animation
            // runs. Only the platform's finished event requests the actual move.
            if (!trip.MoveRequested) return;
            if (!trip.Loading.IsCompleted || !trip.Target.IsFullyLoaded) return;
            Physics.SyncTransforms();
            if (!HasFloor(trip.Destination)) return;
            trip.AwaitingArrival = true;
            trip.Deadline = Time.unscaledTime + 10f;
            try {
                executing = true;
                // Preloading has already updated the server's manager. Send the
                // original packet explicitly: LoadFromPosition would be a no-op.
                if (trip.DestinationSync != null) {
                    object sync = typeof(PlayerChunkManager).GetField("syncChunks", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(trip.Manager);
                    sync.GetType().GetMethod("SendToPlayer").Invoke(sync, new object[] { trip.Player, trip.DestinationSync });
                }
                if (trip.Body.Entity != null) trip.Body.Entity.Chunk = trip.Target;
                trip.Body.ForceTeleportTo(trip.Destination);
                loadPosition.Invoke(trip.Manager, new object[] { trip.Destination });
                if (log != null) log("Destination loaded; awaiting teleport arrival for " + trip.Player.Identifier + ".");
            } finally { executing = false; }
        }
        static readonly Vector3[] floorOffsets = { Vector3.zero, new Vector3(.25f,0,0), new Vector3(-.25f,0,0), new Vector3(0,0,.25f), new Vector3(0,0,-.25f) };
        static bool HasFloor(Vector3 destination)
        {
            // Match housing's footprint check; movable items and players aren't floors.
            foreach (Vector3 offset in floorOffsets) {
                bool solid = false;
                foreach (RaycastHit hit in Physics.RaycastAll(destination + offset + Vector3.up * .25f, Vector3.down, 3.75f,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) {
                    Collider collider = hit.collider;
                    if (collider != null && collider.enabled && collider.gameObject.activeInHierarchy && !collider.isTrigger &&
                        hit.normal.y >= .6f && hit.distance >= .1f && collider.GetComponentInParent<Pickup>() == null &&
                        collider.GetComponentInParent<PlayerController>() == null) { solid = true; break; }
                }
                if (!solid) return false;
            }
            return true;
        }
        static void RestoreOrigin(Trip trip)
        {
            if (trip.Player == null || trip.Player.IsBeingDestroyed || trip.Body == null) return;
            if (trip.Started) loadPosition.Invoke(trip.Manager, new object[] { trip.MoveRequested ? trip.Origin : trip.Body.transform.position });
            if (!trip.MoveRequested) return;
            // The customization platform may already have faded the client to black.
            // Reissue the safe departure location to restore its normal native fade-in.
            try { executing = true; trip.Body.ForceTeleportTo(trip.Origin); }
            finally { executing = false; }
        }
        static void Cancel(Trip trip, string reason, bool notify = true)
        {
            trips.Remove(trip.Manager);
            try { RestoreOrigin(trip); }
            catch (Exception e) { if (log != null) log("Could not restore departure: " + e.Message); }
            finally { Release(trip); }
            if (log != null) log("Teleport canceled: " + reason + ".");
            if (notify) Notify(trip.Player, "Teleport canceled: destination could not be loaded safely. Please try again or ask an admin to check its floor.");
        }
        static void Notify(Player player, string message)
        {
            try {
                if (player != null && !player.IsBeingDestroyed && PlayerCommunicationManager.Instance != null)
                    PlayerCommunicationManager.Instance.SendMessageToPlayer(player, message, 5f);
            } catch (Exception e) { if (log != null) log("Teleport message unavailable: " + e.Message); }
        }
        internal static void Shutdown()
        {
            foreach (Trip trip in new List<Trip>(trips.Values)) Cancel(trip, "server mod stopping");
            if (harmony != null) harmony.UnpatchSelf();
            harmony = null;
        }
    }
}
