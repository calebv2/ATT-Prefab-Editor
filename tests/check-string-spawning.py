"""Exercise the actual selected-string UI spawn action and its asynchronous result."""
from pathlib import Path
import re
import subprocess
import tempfile


repo = Path(__file__).resolve().parents[1]
selection = (repo / 'client/player-kit/mod/src/EditSelection.cs').read_text(encoding='utf-8')


def method(name):
    found = re.search(r'^        void ' + re.escape(name) + r'\(', selection, re.M)
    if found is None:
        raise AssertionError('Production method missing: ' + name)
    start = found.start()
    opening = selection.index('{', found.end())
    depth = 0
    # Braces inside C# comments, ordinary/verbatim strings and chars are data.
    tokens = r'//[^\n]*|/\*[\s\S]*?\*/|@"(?:""|[^"])*"|"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\'|[{}]'
    for token in re.finditer(tokens, selection[opening:]):
        if token.group() == '{':
            depth += 1
        elif token.group() == '}':
            depth -= 1
            if depth == 0:
                return selection[start:opening + token.end()]
    raise AssertionError('Production method is not balanced: ' + name)


harness = r'''
using System;
using System.Collections.Generic;

namespace UnityEngine
{
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float a, float b, float c) { x = a; y = b; z = c; }
        public static Vector3 operator +(Vector3 a, Vector3 b) { return new Vector3(a.x+b.x, a.y+b.y, a.z+b.z); }
        public static Vector3 operator *(Vector3 a, float n) { return new Vector3(a.x*n, a.y*n, a.z*n); }
    }
    public class Transform { public Vector3 position, forward; }
    public class Camera { public Transform transform = new Transform(); }
}

namespace PrefabEditorMod
{
    using UnityEngine;
    class Entity { public uint Id; }
    class EditResult { public bool Ok; public Entity Ent; public string Error; }
    class Api
    {
        public int Calls;
        public float X, Y, Z;
        public string Data;
        public Action<EditResult> Pending;
        public void Paste(float x, float y, float z, string data, Action<EditResult> callback)
        { Calls++; X=x; Y=y; Z=z; Data=data; Pending=callback; }
        public void Complete(EditResult result)
        {
            Action<EditResult> callback = Pending;
            Pending = null;
            if (callback == null) throw new Exception("No pending paste request");
            callback(result);
        }
    }
    class Test
    {
        const string A = "123,0,0,1065353216,0,0,0,0,0,0,0,";
        const string B = "456,0,0,1065353216,0,0,0,0,0,0,0,";
        bool _busy, _importing;
        bool buildAllowed = true;
        int _selectedString;
        List<StringLibraryEntry> _stringLibrary;
        Camera Camera;
        Api _api = new Api();
        string Status = "unchanged";
        int HistoryRefreshes, StatusTone;
        uint Adopted;
        const int ToneOk = 1, ToneErr = 2;
        bool CanOpenTab(int tab) { Check(tab==2, "selected-string spawn requires Build permission"); return buildAllowed; }
        static bool IsSaveString(string data) { return StringLibrary.IsSaveString(data); }
        string Reason(EditResult result) { return result.Error ?? "unknown error"; }
        void SetStatus(string text) { Status=text; StatusTone=0; }
        void SetStatus(string text, int tone) { Status=text; StatusTone=tone; }
        void RefreshHistory(bool show) { Check(!show,"refresh history quietly after string spawn"); HistoryRefreshes++; }
        void AdoptSoon(uint id) { Adopted=id; }

PRODUCTION_METHOD

        static void Check(bool value, string label) { if (!value) throw new Exception(label); }
        static Test Ready()
        {
            var test = new Test();
            test.Camera = new Camera();
            test.Camera.transform.position = new Vector3(10,20,30);
            test.Camera.transform.forward = new Vector3(2,-1,0.5f);
            test._stringLibrary = StringLibrary.Parse("First: "+A+"\nSecond: "+B, "Library", "variants.txt");
            Check(test._stringLibrary.Count==2,"real library parser supplies distinct variants");
            test._selectedString = 1;
            return test;
        }
        static void Guard(Action<Test> configure, string label)
        {
            Test test = Ready();
            configure(test);
            test.SpawnSelectedString();
            Check(test._api.Calls==0 && test.HistoryRefreshes==0 && test.Adopted==0,label);
        }
        static void Main()
        {
            Test success = Ready();
            success.SpawnSelectedString();
            Check(success._api.Calls==1 && success._api.Data==B && success._api.Data!=A,"send only the selected variant exactly once");
            Check(success._api.X==16 && success._api.Y==17 && success._api.Z==31.5f,"spawn is camera position plus forward times three");
            Check(success._busy && success.Status.Contains(success._stringLibrary[1].Name),"pending spawn is busy and names selected variant");
            success.SpawnSelectedString();
            Check(success._api.Calls==1,"second click during the pending request cannot duplicate the item");
            success._api.Complete(new EditResult { Ok=true, Ent=new Entity { Id=77 } });
            Check(!success._busy && success.Adopted==77 && success.HistoryRefreshes==1,"successful result adopts spawned id and refreshes history");
            Check(success.StatusTone==ToneOk && success.Status.Contains("#77") && success.Status.Contains(success._stringLibrary[1].Name),"success identifies the spawned selected variant");

            Guard(t=>t._busy=true,"busy guard prevents dispatch");
            Guard(t=>t._importing=true,"import-in-progress guard prevents dispatch");
            Guard(t=>t.buildAllowed=false,"missing Build permission prevents dispatch");
            Guard(t=>t.Camera=null,"missing camera prevents dispatch");
            Guard(t=>t._selectedString=-1,"missing selection prevents dispatch");
            Guard(t=>t._selectedString=t._stringLibrary.Count,"past-end selection prevents dispatch");
            Guard(t=>{t._stringLibrary.Clear();t._selectedString=0;},"empty library prevents dispatch");
            Test invalid = Ready();
            invalid._stringLibrary[1].SaveString="not a save string";
            invalid.SpawnSelectedString();
            Check(invalid._api.Calls==0 && !invalid._busy && invalid.StatusTone==ToneErr && invalid.Status.Contains("valid"),"invalid saved data is refused with a useful message");

            Test failure = Ready();
            failure.SpawnSelectedString();
            failure._api.Complete(new EditResult { Error="server rejected this prefab" });
            Check(!failure._busy && failure.StatusTone==ToneErr && failure.Status.Contains("server rejected this prefab"),"failure clears busy and retains server reason");
            Check(failure.Status.Contains("Check the world") && failure._api.Calls==1 && failure.Adopted==0 && failure.HistoryRefreshes==0,"ambiguous failure never retries or adopts automatically");

            Test missingId = Ready();
            missingId.SpawnSelectedString();
            missingId._api.Complete(new EditResult { Ok=true });
            Check(!missingId._busy && missingId.StatusTone==ToneErr && missingId.Status.Contains("no item id"),"missing entity reports unknown placement result");
            Check(missingId.Status.Contains("Check the world") && missingId._api.Calls==1 && missingId.Adopted==0,"missing entity never causes an automatic second paste");
            Test zeroId = Ready();
            zeroId.SpawnSelectedString();
            zeroId._api.Complete(new EditResult { Ok=true, Ent=new Entity { Id=0 } });
            Check(!zeroId._busy && zeroId.StatusTone==ToneErr && zeroId.Adopted==0 && zeroId._api.Calls==1,"zero entity id is unconfirmed and cannot trigger a second paste");
            Console.WriteLine("PASS: production selected-string spawn, exact variant/position, guards, pending-click isolation, success adoption/history, failure and missing-id handling without retries.");
        }
    }
}
'''.replace('PRODUCTION_METHOD', method('SpawnSelectedString'))

with tempfile.TemporaryDirectory(prefix='prefab-string-spawn-') as folder:
    root = Path(folder)
    (root / 'Test.cs').write_text(harness, encoding='utf-8')
    subprocess.run([
        r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe',
        '/nologo', '/warn:0', '/out:' + str(root / 'test.exe'),
        str(repo / 'client/player-kit/mod/src/StringLibrary.cs'), str(root / 'Test.cs')
    ], check=True)
    subprocess.run([str(root / 'test.exe')], check=True)
