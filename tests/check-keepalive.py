"""Compile the actual keep-alive methods with a deterministic clock/transport."""
from pathlib import Path
import re
import subprocess
import tempfile

source = (Path(__file__).resolve().parents[1] / 'client/player-kit/mod/src/EditSelection.cs').read_text(encoding='utf-8-sig')
def method(name):
    start = re.search(r'        (?:public )?void ' + name + r'\(', source).start()
    end = source.index('{', start) + 1
    depth = 1
    while depth:
        depth += (source[end] == '{') - (source[end] == '}')
        end += 1
    return source[start:end]

harness = r'''
using System;
static class Time { public static float unscaledTime; }
class EditResult { public bool Ok; }
class Api {
 public int Calls, Direction; public string Player; public Action<EditResult> Callback;
 public void NudgePlayer(string player,int direction,Action<EditResult> cb) { Calls++; Player=player; Direction=direction; Callback=cb; }
 public void Finish(bool ok) { var cb=Callback; Callback=null; cb(new EditResult { Ok=ok }); }
}
class Test {
 string _keepAlivePlayer="Alice", _workMessage="";
 float _keepAliveNextAt=300;
 int _keepAliveDirection=1, _keepAliveGeneration;
 bool _keepAliveBusy, _workBusy;
 Api _api=new Api();
 static string Reason(EditResult r) { return "offline"; }
 static void Assert(bool value,string message) { if(!value) throw new Exception(message); }
 METHODS
 static void Main() {
  var t=new Test();
  Time.unscaledTime=299; t.TickKeepAlive(); Assert(t._api.Calls==0,"must wait five minutes");
  Time.unscaledTime=300; t.TickKeepAlive(); Assert(t._api.Calls==1 && t._api.Player=="Alice","chosen player at five minutes");
  t.TickKeepAlive(); Assert(t._api.Calls==1,"no duplicate request while pending");
  t._api.Finish(true); Assert(t._keepAliveNextAt==600 && t._keepAliveDirection==-1,"schedule and alternate");
  Time.unscaledTime=600; t._workBusy=true; t.TickKeepAlive(); Assert(t._api.Calls==1,"defer during manual teleport");
  t._workBusy=false; t.TickKeepAlive(); Assert(t._api.Direction==-1,"second nudge opposite direction");
  t.StopKeepAlive(); t._api.Finish(true); Assert(t._keepAlivePlayer=="" && t._keepAliveDirection==-1,"stale callback cannot resume stopped timer");
  Time.unscaledTime=1000; t.TickKeepAlive(); Assert(t._api.Calls==2,"no movement after stop");
  t._keepAlivePlayer="Bob"; t.TickKeepAlive(); t._api.Finish(false);
  Assert(t._keepAlivePlayer=="" && t._workMessage.Contains("offline"),"failure stops timer with explanation");
  Console.WriteLine("PASS: five-minute timing, player binding, alternating direction, pending requests, manual teleport deferral, stop and failure.");
 }
}
'''.replace('METHODS', method('StopKeepAlive') + '\n' + method('TickKeepAlive'))
with tempfile.TemporaryDirectory(prefix='prefab-keepalive-test-') as folder:
    root = Path(folder)
    cs = root / 'Test.cs'
    exe = root / 'Test.exe'
    cs.write_text(harness)
    subprocess.run([r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe', '/nologo', '/out:' + str(exe), str(cs)], check=True)
    subprocess.run([str(exe)], check=True)
