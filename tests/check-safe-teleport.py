"""Compile the real teleport gate against deterministic game/network adapters."""
from pathlib import Path
import subprocess
import tempfile

source = Path(__file__).resolve().parents[1] / 'server/server-kit/core/Modules/SafeTeleport.cs'
harness = r'''
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;
using Alta.Chunks;
using PrefabEditor.Modules;
namespace UnityEngine {
 public struct Vector3 {
  public float x,y,z; public Vector3(float a,float b,float c){x=a;y=b;z=c;}
  public static Vector3 zero=new Vector3(),up=new Vector3(0,1,0),down=new Vector3(0,-1,0);
  public float sqrMagnitude {get{return x*x+y*y+z*z;}}
  public static Vector3 operator +(Vector3 a,Vector3 b){return new Vector3(a.x+b.x,a.y+b.y,a.z+b.z);}
  public static Vector3 operator -(Vector3 a,Vector3 b){return new Vector3(a.x-b.x,a.y-b.y,a.z-b.z);}
  public static Vector3 operator *(Vector3 a,float b){return new Vector3(a.x*b,a.y*b,a.z*b);}
 }
 public class Transform {public Vector3 position;}
 public class GameObject {public bool activeInHierarchy=true;}
 public class Collider {public bool enabled=true,isTrigger; public bool movable; public GameObject gameObject=new GameObject(); public T GetComponentInParent<T>() where T:class {return movable ? new Pickup() as T : null;}}
 public struct RaycastHit {public Collider collider;public Vector3 normal;public float distance;}
 public enum QueryTriggerInteraction {Ignore}
 public static class Time {public static float unscaledTime;}
 public static class Physics {
  public const int DefaultRaycastLayers=1; public static bool floor=true,movable;public static int checks;
  public static void SyncTransforms(){}
  public static RaycastHit[] RaycastAll(Vector3 p,Vector3 d,float distance,int mask,QueryTriggerInteraction q){checks++;return floor?new[]{new RaycastHit{collider=new Collider{movable=movable},normal=Vector3.up,distance=1}}:new RaycastHit[0];}
 }
}
namespace HarmonyLib {
 public class Harmony {public Harmony(string id){} public void Patch(MethodInfo m,HarmonyMethod prefix){} public void UnpatchSelf(){} }
 public class HarmonyMethod {public HarmonyMethod(Type t,string n){} }
 public static class AccessTools {public static MethodInfo Method(Type t,string n){return t.GetMethod(n,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static);} }
}
namespace Alta.Serialization {public class Stream {public bool IsWriting=true;} }
namespace Alta.Chunks {
 public class Chunk {public bool IsForceLoaded,IsFullyLoaded=true;public ChunkPlayerManager Players;public Chunk(){Players=new ChunkPlayerManager{Chunk=this};}}
 public class LocationChunk:Chunk {}
 public class ChunkPlayerManager {public Chunk Chunk; public bool Remove(Player p){object[] args={this,p,false};if(!(bool)Test.Call("RemoveSourcePrefix",args))return (bool)args[2];return p.LoadedChunks.Remove(Chunk);}}
 public static class LocationChunkHelper {public static LocationChunk Target;public static void EnsureChunkLookupIsSetup(){} public static LocationChunk GetBestContaining(Vector3 p){return Target;}}
}
public class Pickup {}
public class PlayerCommunicationManager {public static PlayerCommunicationManager Instance=new PlayerCommunicationManager();public static int Messages;public void SendMessageToPlayer(Player p,string message,float seconds){Messages++;} }
public class NetworkSceneManager {public static bool IsServer=true;}
public class Entity {public Chunk Chunk;}
public class PlayerController {
 public Player NetworkPlayer;public Transform transform=new Transform(); public Entity Entity=new Entity();public bool IsAlive=true;public int Calls;public bool DelayArrival;public Vector3 Last;
 public void ForceTeleportTo(Vector3 p){if(!(bool)Test.Call("TeleportPrefix",new object[]{this,p}))return;Calls++;Last=p;if(!DelayArrival)transform.position=p;}
}
internal struct ChunksToSync {public Chunk currentChunk;public Chunk[] loadedChunks;}
public class FakeSync {internal ChunksToSync Last;public void SendToPlayer(Player p,ChunksToSync packet){Last=packet;}}
public class PlayerChunkManager {
 public LocationChunk Chunk;public Player Player;private FakeSync syncChunks=new FakeSync();public FakeSync Sync {get{return syncChunks;}}
 public void HandleChunkSync(){}
 public void LoadChunkAt(LocationChunk c,Vector3 p){Test.Call("ExternalLoadPrefix",new object[]{this});LoadFromPosition(p);}
 private void LoadFromPosition(Vector3 p){
  object[] pos={this,p};Test.Call("LoadingPositionPrefix",pos);p=(Vector3)pos[1];
  var target=p.x==0?Player.Source:LocationChunkHelper.Target;
  foreach(var c in new List<Chunk>(Player.LoadedChunks))if(c!=target)c.Players.Remove(Player);
  Player.LoadedChunks.Add(target);Chunk=target;Player.PlayerController.Entity.Chunk=target;
  object[] packet={Player,new Alta.Serialization.Stream(),new ChunksToSync{currentChunk=target,loadedChunks=new Chunk[]{target}}};
  Test.Call("ChunkSyncPrefix",new object[]{this,packet[1],packet});syncChunks.SendToPlayer(Player,(ChunksToSync)packet[2]);
 }
 public void Load(Vector3 p){LoadFromPosition(p);}
}
public class Player {
 private TaskCompletionSource<bool> loadStatusTask;public PlayerChunkManager ChunkManager;public PlayerController PlayerController;public LocationChunk Source;public bool IsBeingDestroyed;public string Identifier="test";public HashSet<Chunk> LoadedChunks=new HashSet<Chunk>();
 private Task AwaitRemoteClientToLoadChunk(LocationChunk c,Vector3 p){ChunkManager.LoadChunkAt(c,p);loadStatusTask=new TaskCompletionSource<bool>();return loadStatusTask.Task;}
 public void Ack(){loadStatusTask.SetResult(true);}public void Fail(){loadStatusTask.SetException(new Exception("load error"));}
 public void Replace(){loadStatusTask=new TaskCompletionSource<bool>();}
}
class Test {
 internal static object Call(string name,object[] args){return typeof(SafeTeleport).GetMethod(name,BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,args);}
 static void Check(bool b,string s){if(!b)throw new Exception(s);}
 static Player Make(){
  SafeTeleport.Shutdown();Time.unscaledTime=0;Physics.floor=true;Physics.movable=false;Physics.checks=0;PlayerCommunicationManager.Messages=0;
  LocationChunkHelper.Target=new LocationChunk();var p=new Player{Source=new LocationChunk()};
  p.PlayerController=new PlayerController{NetworkPlayer=p};p.ChunkManager=new PlayerChunkManager{Player=p,Chunk=p.Source};p.PlayerController.Entity.Chunk=p.Source;p.LoadedChunks.Add(p.Source);return p;
 }
 static Vector3 dest=new Vector3(20,3,40);
 static void Start(Player p){SafeTeleport.Request(p.PlayerController,dest);Check(p.PlayerController.Calls==0,"must not move before acknowledgement");}
 static void Tick(float t){Time.unscaledTime=t;SafeTeleport.Tick();}
 static void Main(){
  SafeTeleport.Initialize(null);
  var contact=Make();object platform=new object();
  SafeTeleport.Preload(contact.PlayerController,dest,platform);
  Check(contact.ChunkManager.Chunk==LocationChunkHelper.Target && contact.PlayerController.Calls==0,"contact immediately starts destination loading");
  Tick(1);Check(contact.PlayerController.Calls==0,"contact cannot move before loading completes");
  contact.Ack();Tick(2);Check(contact.PlayerController.Calls==0,"loaded contact waits for native platform animation");
  SafeTeleport.Request(contact.PlayerController,dest);Tick(2.1f);
  Check(contact.PlayerController.Calls==1 && contact.PlayerController.Last.x==20,"finished activation commits ready destination");
  Check(PlayerCommunicationManager.Messages==0,"contact loading and successful teleport show no messages");
  contact=Make();SafeTeleport.Preload(contact.PlayerController,dest,platform);
  SafeTeleport.Request(contact.PlayerController,dest);Tick(.5f);Check(contact.PlayerController.Calls==0,"early activation still waits for destination readiness");
  contact.Ack();Tick(1);Check(contact.PlayerController.Calls==1,"destination readiness commits after activation without extra delay");
  contact=Make();SafeTeleport.Preload(contact.PlayerController,dest,platform);contact.Ack();Tick(31);
  Check(contact.PlayerController.Calls==0 && PlayerCommunicationManager.Messages==0,"abandoned contact expires quietly without moving");
  contact=Make();SafeTeleport.Preload(contact.PlayerController,dest,platform);contact.Ack();
  SafeTeleport.CancelPreload(contact.PlayerController,new object());Check(contact.Source.IsForceLoaded,"unrelated platform cannot cancel");
  SafeTeleport.CancelPreload(contact.PlayerController,platform);Tick(2);
  Check(contact.PlayerController.Calls==0 && !contact.Source.IsForceLoaded && contact.ChunkManager.Chunk==contact.Source,"stepping off cancels without moving player");
  contact=Make();SafeTeleport.Preload(contact.PlayerController,dest,platform);
  SafeTeleport.CancelPreload(contact.PlayerController,platform);contact.Ack();Tick(3);
  Check(contact.PlayerController.Calls==0 && contact.ChunkManager.Chunk==contact.Source,"late loading acknowledgement after step-off cannot teleport");
  var p=Make();Start(p);Check(p.Source.IsForceLoaded && p.LoadedChunks.Contains(p.Source),"source must remain loaded and subscribed");
  Check(p.ChunkManager.Sync.Last.currentChunk==p.Source && p.ChunkManager.Sync.Last.loadedChunks.Length==2,"client retains source during preload");
  Tick(1);Check(p.PlayerController.Calls==0,"server load alone cannot move player");
  SafeTeleport.Request(p.PlayerController,dest);p.Ack();LocationChunkHelper.Target.IsFullyLoaded=false;Tick(2);Check(p.PlayerController.Calls==0,"client acknowledgement alone cannot move player");
  LocationChunkHelper.Target.IsFullyLoaded=true;Tick(3);Check(p.PlayerController.Calls==1,"no artificial settling interval once destination is ready");
  Check(p.PlayerController.Calls==1 && p.PlayerController.Last.x==20,"commit once after both loads");
  Check(Physics.checks>=5 && Physics.checks%5==0,"check entire arrival footprint");Check(p.Source.IsForceLoaded,"source retained until arrival is observed");Tick(3.1f);Check(!p.Source.IsForceLoaded && !p.LoadedChunks.Contains(p.Source),"source released after arrival");
  Check(p.ChunkManager.Sync.Last.currentChunk==LocationChunkHelper.Target && p.ChunkManager.Sync.Last.loadedChunks.Length==1,"final destination packet restored");Tick(4);Check(p.PlayerController.Calls==1,"no duplicate commit");
  p=Make();p.PlayerController.DelayArrival=true;SafeTeleport.Preload(p.PlayerController,dest,platform);SafeTeleport.Request(p.PlayerController,dest);p.Ack();Tick(1);
  Check(p.PlayerController.Calls==1 && p.Source.IsForceLoaded,"remote request retains source until client arrives");
  SafeTeleport.CancelPreload(p.PlayerController,platform);p.ChunkManager.Load(Vector3.zero);Tick(2);
  Check(p.Source.IsForceLoaded && p.LoadedChunks.Contains(LocationChunkHelper.Target) && p.PlayerController.Calls==1,"exit and stale remote position cannot cancel or unload destination");
  p.PlayerController.transform.position=dest;Tick(3);Check(!p.Source.IsForceLoaded && p.PlayerController.Calls==1,"remote arrival releases once");
  p=Make();p.PlayerController.DelayArrival=true;Start(p);p.Ack();Tick(1);Tick(12);
  Check(p.PlayerController.Calls==2 && p.PlayerController.Last.x==0 && !p.Source.IsForceLoaded,"missing arrival rolls back instead of hanging");
  p=Make();Start(p);p.Ack();Physics.floor=false;Tick(1);Tick(2);Check(p.PlayerController.Calls==0,"missing floor blocks move");Tick(31);Check(p.PlayerController.Calls==1 && p.PlayerController.Last.x==0 && !p.Source.IsForceLoaded,"timeout restores source");
  p=Make();Start(p);p.Ack();Physics.movable=true;Tick(1);Tick(2);Check(p.PlayerController.Calls==0,"pickup is not a floor");
  p=Make();Start(p);p.Fail();Tick(1);Check(p.PlayerController.Calls==1 && p.PlayerController.Last.x==0,"fault restores source");
  p=Make();Start(p);p.Replace();Tick(1);Check(p.PlayerController.Calls==0 && !p.Source.IsForceLoaded,"native transition owns changed request");
  p=Make();Start(p);p.IsBeingDestroyed=true;Tick(1);Check(p.PlayerController.Calls==0 && !p.Source.IsForceLoaded,"disconnect releases resources");
  p=Make();LocationChunkHelper.Target=p.Source;p.PlayerController.ForceTeleportTo(new Vector3(.12f,0,0));Check(p.PlayerController.Calls==1,"keepalive nudge remains immediate");
  p=Make();p.PlayerController.ForceTeleportTo(dest);Check(p.PlayerController.Calls==1 && !p.Source.IsForceLoaded,"foreign teleport bypasses Prefabulator gate");
  p=Make();SafeTeleport.Preload(p.PlayerController,dest,platform);p.Ack();p.ChunkManager.LoadChunkAt(p.Source,Vector3.zero);Tick(2);Check(!p.Source.IsForceLoaded && p.ChunkManager.Chunk==p.Source && p.PlayerController.Calls==0,"housing load takes over without stale pin or rollback");
  p=Make();SafeTeleport.Preload(p.PlayerController,dest,platform);SafeTeleport.CancelPreload(p.PlayerController,platform);Tick(1);Check(p.Source.IsForceLoaded && p.LoadedChunks.Contains(LocationChunkHelper.Target),"step-off retains chunk until acknowledgement can finish");p.Ack();Tick(2);Check(!p.Source.IsForceLoaded && p.ChunkManager.Chunk==p.Source,"step-off drains request quietly");Start(p);p.Ack();Tick(3);Tick(4);Check(p.PlayerController.Calls==1,"linked teleport reusable after canceled preload");
  p=Make();p.Replace();SafeTeleport.Preload(p.PlayerController,dest,platform);Tick(1);Check(!p.Source.IsForceLoaded,"prior native load not overwritten");p.Ack();Tick(2);Check(p.Source.IsForceLoaded,"preload starts once prior native acknowledgement finishes");p.Ack();SafeTeleport.Request(p.PlayerController,dest);Tick(3);Tick(4);Check(p.PlayerController.Calls==1,"linked teleport succeeds after previous native travel");
  SafeTeleport.Shutdown();Console.WriteLine("PASS: client/server readiness, source retention, packet restoration, floor footprint, duplicate requests, timeout, fault, transition ownership, disconnect and keepalive.");
 }
}
'''
# The test-only packet is public to allow the public fake sender's signature.
harness = harness.replace('internal struct ChunksToSync', 'public struct ChunksToSync')
with tempfile.TemporaryDirectory(prefix='prefab-safe-tp-') as folder:
    root = Path(folder)
    (root/'Test.cs').write_text(harness)
    subprocess.run([r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe', '/nologo', '/warn:0', '/out:'+str(root/'test.exe'), str(source), str(root/'Test.cs')], check=True)
    subprocess.run([str(root/'test.exe')], check=True)
