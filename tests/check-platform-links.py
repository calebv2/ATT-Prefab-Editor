"""Compile actual platform-link storage and dispatch with deterministic game stubs."""
from pathlib import Path
import shutil
import subprocess
import tempfile
import urllib.request
import zipfile
import io
source=Path(__file__).resolve().parents[1]/'server/server-kit/core/Modules/TeleporterLinks.cs'
json_dll=Path(r'C:/Users/caleb/Desktop/TavernLauncher-Server-v1.8.4/game-source/A Township Tale_Data/Managed/Newtonsoft.Json.dll')
harness=r'''
using System;
using System.IO;
using System.Reflection;
using PrefabEditor.Modules;
namespace UnityEngine {
 public static class Time {public static float unscaledTime;}
 public struct Vector3 { public float x,y,z; public Vector3(float a,float b,float c){x=a;y=b;z=c;} public float sqrMagnitude {get{return x*x+y*y+z*z;}} public static Vector3 operator -(Vector3 a,Vector3 b){return new Vector3(a.x-b.x,a.y-b.y,a.z-b.z);} }
 public class Transform { public Vector3 position; }
}
namespace Alta.Networking { public class NetworkEntity { public NetworkEntity PrefabRoot; public Prefab Prefab=new Prefab(); public UnityEngine.Transform transform=new UnityEngine.Transform(); } public class Prefab {public uint Hash=60372;} }
namespace Alta.Networking.Scripts.Player { public interface IPlayer { PlayerController PlayerController {get;} } }
namespace Alta.Serialization { public class Stream {public bool IsReading=true;} }
namespace HarmonyLib {
 public class Harmony {public Harmony(string id){} public void Patch(MethodInfo target,HarmonyMethod prefix=null,HarmonyMethod postfix=null){} public void UnpatchSelf(){} }
 public class HarmonyMethod {public HarmonyMethod(Type t,string m){} }
 public static class AccessTools {public static MethodInfo Method(Type t,string n){return t.GetMethod(n);} }
}
public class FromPlayingToCustomization {public Alta.Networking.NetworkEntity Entity; public void SyncPlayer(){} public void ReturnToCustomization(){} }
public class TeleporterPlatform {public Alta.Networking.NetworkEntity Entity; public void SyncPressing(){} public void Entered(){} public void Exited(){} }
namespace PrefabEditor.Modules {internal static class SafeTeleport {internal static void Request(PlayerController p,UnityEngine.Vector3 v){p.ForceTeleportTo(v);}internal static int Preloads,Cancels;internal static UnityEngine.Vector3 Last;internal static bool Preload(PlayerController p,UnityEngine.Vector3 v,object o){Preloads++;Last=v;return true;}internal static void CancelPreload(PlayerController p,object o){Cancels++;}}}
public class NetworkSceneManager {public static bool IsServer=true;}
public class PlayerController {public bool IsAlive=true,IsLocalPlayer=false; public int Calls; public UnityEngine.Vector3 Destination; public void ForceTeleportTo(UnityEngine.Vector3 p){Calls++;Destination=p;} }
class Player : Alta.Networking.Scripts.Player.IPlayer {public static Player Current; public PlayerController PlayerController {get;set;} }
class Test {
 static void Check(bool b,string message){if(!b)throw new Exception(message);}
 static bool Sync(FromPlayingToCustomization platform,Player player,bool reading){return (bool)typeof(TeleporterLinks).GetMethod("SyncPrefix",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{platform,player,new Alta.Serialization.Stream{IsReading=reading}});}
 static Alta.Networking.NetworkEntity Root(float x){return new Alta.Networking.NetworkEntity{transform=new UnityEngine.Transform{position=new UnityEngine.Vector3(x,2,3)}};}
 static void Main(string[] args){
  string dir=args[0]; TeleporterLinks.Initialize(dir,null);
  var root=Root(1); var platform=new FromPlayingToCustomization{Entity=root}; var pc=new PlayerController(); var player=new Player{PlayerController=pc};
  Check(Sync(platform,player,true) && pc.Calls==0,"unlinked platform retains original action");
  var dest=new UnityEngine.Vector3(-596.447f,275.657f,240.869f);
  Check(TeleporterLinks.Set(root,dest)==null,"save link");
  var pad=new TeleporterPlatform{Entity=root};var pressing=new System.Collections.Generic.HashSet<Alta.Networking.Scripts.Player.IPlayer>{player};
  var contact=typeof(TeleporterLinks).GetMethod("ContactPostfix",BindingFlags.NonPublic|BindingFlags.Static);
  contact.Invoke(null,new object[]{pad,player,new Alta.Serialization.Stream{IsReading=true},pressing});
  Check(SafeTeleport.Preloads==1 && SafeTeleport.Last.x==dest.x && pc.Calls==0,"touch immediately preloads saved destination without moving");
  contact.Invoke(null,new object[]{pad,player,new Alta.Serialization.Stream{IsReading=false},pressing});Check(SafeTeleport.Preloads==1,"outgoing contact cannot preload");
  pressing.Clear();contact.Invoke(null,new object[]{pad,player,new Alta.Serialization.Stream{IsReading=true},pressing});Check(SafeTeleport.Cancels==1,"step off cancels preload");
  Check(!Sync(platform,player,true) && pc.Calls==1 && pc.Destination.x==dest.x,"linked activation redirects");
  Check(Sync(platform,player,false) && pc.Calls==1,"outgoing serialization unchanged");
  NetworkSceneManager.IsServer=false; Check(Sync(platform,player,true) && pc.Calls==1,"client cannot dispatch"); NetworkSceneManager.IsServer=true;
  TeleporterLinks.Shutdown(); TeleporterLinks.Initialize(dir,null);
  platform.Entity=Root(1); Check(!Sync(platform,player,true) && pc.Calls==2,"restart and new entity identity restore link");
  platform.Entity.Prefab.Hash=7; Check(Sync(platform,player,true),"different prefab at same location unchanged"); platform.Entity.Prefab.Hash=60372;
  platform.Entity.transform.position=new UnityEngine.Vector3(10,2,3); Check(Sync(platform,player,true),"moving requires relink");
  Check(TeleporterLinks.Set(platform.Entity,new UnityEngine.Vector3(20,30,40))==null,"relink saved");
  Check(!Sync(platform,player,true) && pc.Destination.x==20,"new destination applied");
  pc.IsAlive=false; Check(Sync(platform,player,true),"dead player unchanged"); pc.IsAlive=true;
  string path=Path.Combine(dir,"teleporter-links.json"); string before=File.ReadAllText(path);
  Check(TeleporterLinks.Set(platform.Entity,new UnityEngine.Vector3(float.NaN,0,0))!=null && before==File.ReadAllText(path),"invalid link cannot overwrite stored links");
  TeleporterLinks.Shutdown(); File.WriteAllText(path,"invalid"); TeleporterLinks.Initialize(dir,null);
  Check(TeleporterLinks.Set(root,dest)!=null && File.ReadAllText(path)=="invalid","corrupt file preserved and rejected");
  TeleporterLinks.Shutdown(); File.Delete(path); TeleporterLinks.Initialize(dir,null); Directory.CreateDirectory(path+".tmp");
  Check(TeleporterLinks.Set(root,dest)!=null && Sync(new FromPlayingToCustomization{Entity=root},player,true),"save failure never activates link");
  Console.WriteLine("PASS: linked/unlinked dispatch, native destination, restart, prefab identity, movement, validation, corrupt data and failed writes.");
 }
}
'''
with tempfile.TemporaryDirectory(prefix='prefab-platform-test-') as folder:
    root=Path(folder)
    # Use the signed desktop build in this .NET Framework harness; the game DLL
    # is patched for Mono and cannot pass Framework strong-name validation.
    package = urllib.request.urlopen("https://api.nuget.org/v3-flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg").read()
    with zipfile.ZipFile(io.BytesIO(package)) as archive:
        (root/json_dll.name).write_bytes(archive.read("lib/net45/Newtonsoft.Json.dll"))
    (root/'Test.cs').write_text(harness)
    exe=root/'Test.exe'
    subprocess.run([r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe','/nologo','/out:'+str(exe),'/r:'+str(root/json_dll.name),str(source),str(root/'Test.cs')],check=True)
    subprocess.run([str(exe),str(root/'data')],check=True)
