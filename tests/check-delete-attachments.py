"""Exercise production delete/preflight/redo guards with deterministic attachment fixtures."""
from pathlib import Path
import re,subprocess,tempfile
source=Path(__file__).resolve().parents[1]/'server/server-kit/core/Modules/EditorModule.cs'
text=source.read_text(encoding='utf-8')
def method(name):
    start=re.search(r'^        (?:public |static |private )+(?:static )?[\w<>]+ '+name+r'\(',text,re.M).start()
    opening=text.index('{',start)
    token=re.compile(r'//[^\n]*|/\*[\s\S]*?\*/|"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\'|[{}]')
    depth=0
    for match in token.finditer(text,opening):
        if match.group()=='{':depth+=1
        elif match.group()=='}':
            depth-=1
            if depth==0:return text[start:match.end()]
    raise AssertionError(name)
methods='\n'.join(method(n) for n in ['Delete','DeleteGroup','ApplyHistory','VetoMove','VetoDelete','VetoAttachment','IsMounted','IsPlayer'])
harness=r'''
using System;
using System.Collections.Generic;
using UnityEngine;
using Alta.Networking;
namespace UnityEngine {
 public struct Vector3{}
 public class Transform {public Vector3 position;}
 public class Joint {public Rigidbody connectedBody;}
 public class Rigidbody {public NetworkEntity Entity;public T GetComponentInParent<T>() where T:class{return Entity as T;}}
}
namespace Alta.Console {public class CommandAttribute:Attribute{public CommandAttribute(string a,string b){}}}
namespace Alta.Networking {
 public class Prefab {public string name="Smelter";}
 public class NetworkEntity {public uint Identifier;public NetworkEntity PrefabRoot;public Pickup CommonPickup;public Joint[] Joints=new Joint[0];public bool Player,ThrowOnRead,FailSave,Destroyed;public Prefab Prefab=new Prefab();public string OriginalName="fixture";public Transform transform=new Transform();
 public T GetComponent<T>() where T:class {return Player?new PlayerController() as T:null;}
 public T[] GetComponentsInChildren<T>(bool inactive){if(ThrowOnRead)throw new Exception("cannot inspect");return (T[])(object)Joints;}}
}
public class PlayerController{}
public class Pickup {public bool IsDocked,IsInteractedWith;}
namespace PrefabEditor.Modules {
 static class EditJson {public static string Err(string s){return "ERROR: "+s;}public static string Esc(string s){return s;}}
 public static class EditorModule {
  const int MaxHistoryGroup=300;
  internal sealed class HistorySnapshot{public uint Id;public bool Exists=true;}
  internal sealed class HistoryEntry{public List<HistorySnapshot> Before=new List<HistorySnapshot>(),After=new List<HistorySnapshot>();}
  static Dictionary<uint,NetworkEntity> db=new Dictionary<uint,NetworkEntity>();
  static int destroys,commits;
  static string ResolveRoot(uint id,out NetworkEntity root){return db.TryGetValue(id,out root)?null:"missing";}
  static string ResolveGroup(string ids,out List<NetworkEntity> roots,int max){roots=new List<NetworkEntity>();foreach(var id in ids.Split(',')){NetworkEntity r;var err=ResolveRoot(uint.Parse(id),out r);if(err!=null)return err;roots.Add(r);}return null;}
  static string EntityRecord(NetworkEntity r,Vector3 p){return "{}";}
  static string CaptureSerialized(NetworkEntity r,out HistorySnapshot s){s=new HistorySnapshot{Id=r.Identifier};return r.FailSave?"save unavailable":null;}
  static HistorySnapshot AbsentSnapshot(uint id){return new HistorySnapshot{Id=id,Exists=false};}
  static string ValidateHistorySize(List<HistorySnapshot> a,List<HistorySnapshot> b){return null;}
  static void DestroyEntity(NetworkEntity r){r.Destroyed=true;destroys++;}
  static HistorySnapshot RestoreSerialized(HistorySnapshot s){return s;}
  static void CommitHistory(string l,List<HistorySnapshot> a,List<HistorySnapshot> b){commits++;}
  static string ApplyHistoryItem(HistorySnapshot from,HistorySnapshot to){if(!to.Exists)DestroyEntity(db[from.Id]);return null;}
METHODS
  static void Check(bool b,string s){if(!b)throw new Exception(s);}
  static NetworkEntity Make(uint id){var n=new NetworkEntity{Identifier=id};db[id]=n;return n;}
  public static void Main(){
   var smelter=Make(70058);smelter.Joints=new[]{new Joint()};
   Check(VetoMove(smelter)!=null && VetoDelete(smelter)==null,"world anchor blocks movement but allows deletion");
   Check(Delete(70058).Contains("needsConfirm") && destroys==0,"standalone fixture needs confirmation");
   Check(Delete(70058,"confirm").Contains("\"ok\":true") && destroys==1 && commits==1,"confirmed standalone deletion commits undo history");
   var internalPart=new NetworkEntity{PrefabRoot=smelter};smelter.Joints=new[]{new Joint{connectedBody=new Rigidbody{Entity=internalPart}}};Check(VetoDelete(smelter)==null && VetoMove(smelter)==null,"internal joints do not count as mounting");
   var external=Make(2);smelter.Joints=new[]{new Joint{connectedBody=new Rigidbody{Entity=external}}};Check(VetoDelete(smelter)!=null,"external mount remains blocked");
   smelter.Joints=new[]{new Joint{connectedBody=new Rigidbody()}};Check(VetoDelete(smelter)!=null,"connection to unowned external body remains blocked");
   smelter.Joints=new[]{new Joint()};external.CommonPickup=new Pickup{IsInteractedWith=true};int before=destroys;Check(DeleteGroup("70058,2","confirm").StartsWith("ERROR") && destroys==before,"group preflight stays atomic for held item");
   external.CommonPickup.IsInteractedWith=false;external.CommonPickup.IsDocked=true;Check(VetoDelete(external)!=null,"docked remains blocked");external.CommonPickup=null;external.Player=true;Check(VetoDelete(external)!=null,"player remains blocked");external.Player=false;
   smelter.FailSave=true;Check(Delete(70058,"confirm").Contains("undoable") && destroys==before,"missing undo save blocks destructive action");smelter.FailSave=false;
   Check(DeleteGroup("70058,2").Contains("needsConfirm") && destroys==before,"group confirmation remains mandatory");Check(DeleteGroup("70058,2","confirm").Contains("\"ok\":true") && destroys==before+2,"world anchored fixture no longer rejects whole group");
   var history=new HistoryEntry();history.Before.Add(new HistorySnapshot{Id=70058});history.After.Add(new HistorySnapshot{Id=70058,Exists=false});Check(ApplyHistory(history,false)==null,"redo deletion uses delete policy");
   smelter.ThrowOnRead=true;Check(VetoDelete(smelter)!=null,"uninspectable attachment remains blocked");
   Console.WriteLine("PASS: standalone/world anchors, internal/external mounts, single/group confirmation, atomic preflight, held/docked/player protection, undo capture and redo deletion.");
  }
 }
}
'''.replace('METHODS',methods)
with tempfile.TemporaryDirectory(prefix='prefab-delete-') as folder:
    root=Path(folder);(root/'Test.cs').write_text(harness)
    subprocess.run([r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe','/nologo','/warn:0','/out:'+str(root/'test.exe'),str(root/'Test.cs')],check=True)
    subprocess.run([str(root/'test.exe')],check=True)
