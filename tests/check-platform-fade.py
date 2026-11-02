"""Compile the production platform fade recovery against focused native stubs."""
from pathlib import Path
import subprocess
import tempfile
source=Path(__file__).resolve().parents[1]/'client/player-kit/mod/src/PlatformFadeRecovery.cs'
harness=r'''
using System;
using System.Reflection;
using UnityEngine;
using PrefabEditorMod;
namespace UnityEngine {
 public struct Vector3 { public float x;public Vector3(float n){x=n;}public float sqrMagnitude{get{return x*x;}}public static Vector3 operator -(Vector3 a,Vector3 b){return new Vector3(a.x-b.x);} }
 public class Transform {public Vector3 position;}
 public static class Time {public static float unscaledTime;}
}
namespace HarmonyLib {
 public class Harmony {public Harmony(string id){}public void Patch(MethodInfo m,HarmonyMethod prefix=null,HarmonyMethod postfix=null){}public void UnpatchSelf(){} }
 public class HarmonyMethod {public HarmonyMethod(Type t,string n){} }
 public static class AccessTools {public static FieldInfo Field(Type t,string n){return t.GetField(n,BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.Instance);}public static MethodInfo Method(Type t,string n){return t.GetMethod(n,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance);} }
}
namespace Alta.Chunks {
 public class LocationChunk {public bool IsFullyLoaded=true;}
 public static class LocationChunkHelper {public static LocationChunk Target;public static LocationChunk GetBestContaining(Vector3 p){return Target;}}
}
namespace Alta.Alchemy {
 public class EffectHandler {public enum TeleportType {Normal}public bool isTeleportable=true;}
 public class PlayerEffectController:EffectHandler {public PlayerController Player;public int Calls;public void Teleport(Vector3 p,float power,TeleportType type){if(!Test.Prefix(this,Player,p))return;Calls++;Player.transform.position=p;}}
}
public class FromPlayingToCustomization {public void ReturnToCustomization(){}}
public class PlayerScreenFader {public bool Locked;public int Fades;private bool IsFadeLocked(){return Locked;}public void FadeInIfNotLocked(float t){if(!Locked)Fades++;}}
public class PlayerController {public static PlayerController Current;public bool IsLocalPlayer=true,IsAlive=true;public Transform transform=new Transform();public PlayerScreenFader ScreenFader=new PlayerScreenFader();}
class Test {
 internal static bool Prefix(Alta.Alchemy.PlayerEffectController e,PlayerController p,Vector3 v){return (bool)typeof(PlatformFadeRecovery).GetMethod("TeleportPrefix",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{e,p,e.isTeleportable,v,1f,Alta.Alchemy.EffectHandler.TeleportType.Normal});}
 static void Request(PlayerController p,float x){Prefix(new Alta.Alchemy.PlayerEffectController{Player=p},p,new Vector3(x));}
 static void Call(string name,params object[] args){typeof(PlatformFadeRecovery).GetMethod(name,BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,args);}
 static void Check(bool b,string m){if(!b)throw new Exception(m);}
 static PlayerController Make(){PlatformFadeRecovery.Shutdown();Time.unscaledTime=0;Alta.Chunks.LocationChunkHelper.Target=new Alta.Chunks.LocationChunk();return PlayerController.Current=new PlayerController();}
 static void Move(PlayerController p,float x){p.transform.position=new Vector3(x);Request(p,x);}
 static void Main(){
  PlatformFadeRecovery.Initialize(null);
  var p=Make();Move(p,20);PlatformFadeRecovery.Tick();Check(p.ScreenFader.Fades==0,"ordinary teleport untouched");
  p=Make();Call("PlatformPrefix",p);Move(p,20);PlatformFadeRecovery.Tick();PlatformFadeRecovery.Tick();Check(p.ScreenFader.Fades==1,"preloaded destination fades back exactly once");
  p=Make();Call("PlatformPrefix",p);Move(p,20);Alta.Chunks.LocationChunkHelper.Target.IsFullyLoaded=false;PlatformFadeRecovery.Tick();Check(p.ScreenFader.Fades==0,"no fade before loaded");
  Alta.Chunks.LocationChunkHelper.Target.IsFullyLoaded=true;p.ScreenFader.Locked=true;PlatformFadeRecovery.Tick();Check(p.ScreenFader.Fades==0,"native locks respected");p.ScreenFader.Locked=false;PlatformFadeRecovery.Tick();Check(p.ScreenFader.Fades==1,"ready and unlocked recovers");
  p=Make();Call("PlatformPrefix",p);Request(p,20);PlatformFadeRecovery.Tick();Check(p.ScreenFader.Fades==0,"ignored teleport cannot reveal source");p.transform.position=new Vector3(20);PlatformFadeRecovery.Tick();Check(p.ScreenFader.Fades==1,"observed arrival required");
  p=Make();Call("PlatformPrefix",p);Move(p,0);PlatformFadeRecovery.Tick();Check(p.ScreenFader.Fades==1,"server rollback releases fade at loaded origin");
  p=Make();Call("PlatformPrefix",p);Move(p,20);PlayerController.Current=new PlayerController();PlatformFadeRecovery.Tick();Check(p.ScreenFader.Fades==0,"native customization controller change untouched");
  p=Make();Call("PlatformPrefix",p);Time.unscaledTime=46;PlatformFadeRecovery.Tick();Move(p,20);PlatformFadeRecovery.Tick();Check(p.ScreenFader.Fades==0,"expired activation cannot affect future teleport");
  p=Make();Call("PlatformPrefix",p);PlatformFadeRecovery.Tick();Check(p.ScreenFader.Fades==0,"unlinked platform never forcibly faded");
  p=Make();Call("PlatformPrefix",p);var effect=new Alta.Alchemy.PlayerEffectController{Player=p,isTeleportable=false};effect.Teleport(new Vector3(20),1f,Alta.Alchemy.EffectHandler.TeleportType.Normal);PlatformFadeRecovery.Tick();Check(effect.Calls==0 && p.ScreenFader.Fades==0,"native cooldown queues linked move without revealing source");effect.isTeleportable=true;PlatformFadeRecovery.Tick();Check(effect.Calls==1 && p.ScreenFader.Fades==1,"queued move executes and fades once when cooldown ends");PlatformFadeRecovery.Tick();Check(effect.Calls==1,"no duplicate cooldown retry");
  Console.WriteLine("PASS: loaded destination, chunk readiness, native locks, arrival, rollback, ordinary teleports, customization and expiration.");
 }
}
'''
with tempfile.TemporaryDirectory(prefix='prefab-fade-') as folder:
    root=Path(folder)
    (root/'Test.cs').write_text(harness)
    subprocess.run([r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe','/nologo','/out:'+str(root/'test.exe'),str(source),str(root/'Test.cs')],check=True)
    subprocess.run([str(root/'test.exe')],check=True)
