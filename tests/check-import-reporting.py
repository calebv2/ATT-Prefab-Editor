"""Exercise the production import queue's placement/rotation accounting and diagnostics."""
from pathlib import Path
import re,subprocess,tempfile
text=(Path(__file__).resolve().parents[1]/'client/player-kit/mod/src/EditSelection.cs').read_text(encoding='utf-8')
def method(name):
    start=re.search(r'^        void '+name+r'\(',text,re.M).start();opening=text.index('{',start);depth=0
    for match in re.finditer(r'//[^\n]*|/\*[\s\S]*?\*/|"(?:\\.|[^"\\])*"|[{}]',text[opening:]):
        if match.group()=='{':depth+=1
        elif match.group()=='}':
            depth-=1
            if depth==0:return text[start:opening+match.end()]
    raise AssertionError(name)
harness=r'''
using System;
using System.Collections.Generic;
namespace UnityEngine {public struct Vector3 {public float x,y,z;public static Vector3 zero{get{return new Vector3();}}public static Vector3 operator +(Vector3 a,Vector3 b){return a;}public static Vector3 operator -(Vector3 a,Vector3 b){return a;}public static Vector3 operator *(Vector3 a,float b){return a;}}public class Transform{public Vector3 position,forward;}public class Camera{public Transform transform=new Transform();}public static class Mathf{public static float Abs(float n){return Math.Abs(n);}}}
namespace PrefabEditorMod {
using UnityEngine;
class EditResult {public bool Ok;public Entity Ent;public string Error;}
class Entity {public uint Id;}
class Api {public Queue<EditResult> Pasted=new Queue<EditResult>(),Rotated=new Queue<EditResult>();public int Rotations;public void Paste(float x,float y,float z,string s,Action<EditResult> cb){cb(Pasted.Dequeue());}public void Rotate(uint id,float x,float y,float z,Action<EditResult> cb){Rotations++;cb(Rotated.Dequeue());}}
class Test {
 class ImportedPrefab {public string Name,SaveString="SAVE";public Vector3 Position,Rotation;}
 bool _importing,_importAtCamera;List<ImportedPrefab> _importEntries=new List<ImportedPrefab>();Queue<ImportedPrefab> _importQueue;
 int _importTotal,_importDone,_importFailed,_importRotationFailed;string _importFirstFailure="",Status;List<uint> _importSpawnedIds=new List<uint>();Vector3 _importOffset;Camera Camera;Api _api=new Api();Action<string> _log;List<string> Logs=new List<string>();const int ToneOk=1,ToneErr=2;
 string Reason(EditResult r){return r.Error;}void SetStatus(string text){Status=text;}void SetStatus(string text,int tone){Status=text;}void AdoptManySoon(List<uint> ids,string status){}
METHODS
 static void Check(bool ok,string text){if(!ok)throw new Exception(text);}
 static EditResult Placed(uint id){return new EditResult{Ok=true,Ent=new Entity{Id=id}};}
 static void Main(){var t=new Test();t._log=t.Logs.Add;
 t._importEntries.Add(new ImportedPrefab{Name="Wall"});t._importEntries.Add(new ImportedPrefab{Name="Smelter",Rotation=new Vector3{y=90}});t._importEntries.Add(new ImportedPrefab{Name="Broken"});t._importEntries.Add(new ImportedPrefab{Name="No entity"});
 t._api.Pasted.Enqueue(Placed(1));t._api.Pasted.Enqueue(Placed(2));t._api.Pasted.Enqueue(new EditResult{Error="unknown prefab"});t._api.Pasted.Enqueue(new EditResult{Ok=true});t._api.Rotated.Enqueue(new EditResult{Error="mounted"});
 t.StartImport();Check(!t._importing && t._importDone==2 && t._importFailed==2 && t._importRotationFailed==1,"count actually placed objects separately from failed rotation");Check(t._importSpawnedIds.Count==2 && t._api.Rotations==1,"keep spawned selection and rotate only successful spawn");
 Check(t.Logs.Count==3 && t.Logs[0].Contains("Smelter #2") && t.Logs[0].Contains("item was placed") && t.Logs[0].Contains("mounted"),"rotation diagnostic names spawned item and error");Check(t.Logs[1].Contains("Broken") && t.Logs[1].Contains("unknown prefab"),"placement failure preserves name and reason");Check(t.Logs[2].Contains("server returned no spawned entity"),"missing entity explicit");Check(t.Status.Contains("2 placed, 2 not placed") && t.Status.Contains("rotation error") && t.Status.Contains("First error: Smelter"),"completion keeps accurate totals and first failure visible");
 t._importEntries.Clear();t._importEntries.Add(new ImportedPrefab{Name="Good"});t._api.Pasted.Enqueue(Placed(3));t.StartImport();Check(t._importDone==1 && t._importFailed==0 && t._importRotationFailed==0 && !t.Status.Contains("First error"),"next import resets error counters and details");
 Console.WriteLine("PASS: spawn/rotation accounting, partial import selection, item-specific errors, completion detail and reset.");}
}}
'''.replace('METHODS','\n'.join(method(n) for n in ['StartImport','ImportNext','ReportImportFailure']))
with tempfile.TemporaryDirectory(prefix='prefab-import-') as folder:
    root=Path(folder);(root/'Test.cs').write_text(harness)
    subprocess.run([r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe','/nologo','/warn:0','/out:'+str(root/'test.exe'),str(root/'Test.cs')],check=True)
    subprocess.run([str(root/'test.exe')],check=True)
