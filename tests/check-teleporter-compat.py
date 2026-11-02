"""Exercise actual EditApi against a deterministic Unity transport stub."""
from pathlib import Path
import subprocess
import tempfile
source = Path(__file__).resolve().parents[1] / 'client/player-kit/mod/src/EditApi.cs'
harness = r'''
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using PrefabEditorMod;
namespace UnityEngine { public struct Vector3 { public float x,y,z; } }
namespace MelonLoader { static class MelonCoroutines { public static void Start(IEnumerator e) { while(e.MoveNext()) {} } } }
namespace UnityEngine.Networking {
 public class UploadHandlerRaw { public byte[] data; public UploadHandlerRaw(byte[] b) { data=b; } }
 public class DownloadHandlerBuffer { public string text; }
 public class UnityWebRequest {
  public enum Result { Success, Failure }
  public static Queue<string> Replies=new Queue<string>();
  public static List<string> Calls=new List<string>();
  public UploadHandlerRaw uploadHandler; public DownloadHandlerBuffer downloadHandler;
  public int timeout; public Result result; public string error; public long responseCode;
  string url;
  public UnityWebRequest(string u,string m) { url=u; }
  public void SetRequestHeader(string a,string b) {}
  public object SendWebRequest() {
   Calls.Add(url+" "+Encoding.UTF8.GetString(uploadHandler.data));
   string reply=Replies.Dequeue(); result=reply.StartsWith("HTTP:")?Result.Failure:Result.Success;
   downloadHandler.text=reply.Replace("HTTP:",""); error="transport failure"; responseCode=404; return null;
  }
  public void Dispose() {}
 }
}
class Test {
 static void Check(bool b,string message) { if(!b) throw new Exception(message); }
 static string Missing="HTTP:{\"error\":\"no such route: POST /api/edit/link-teleporter\"}";
 static EditResult Run(params string[] replies) {
  UnityEngine.Networking.UnityWebRequest.Calls.Clear();
  UnityEngine.Networking.UnityWebRequest.Replies=new Queue<string>(replies);
  EditResult result=null; int callbacks=0;
  new EditApi("http://test",null).LinkTeleporter(4810,-596.447f,275.657f,240.869f,delegate(EditResult r){ result=r; callbacks++; });
  Check(callbacks==1,"exactly one callback"); return result;
 }
 static EditResult Trade(int mode, params string[] replies) {
  UnityEngine.Networking.UnityWebRequest.Calls.Clear();
  UnityEngine.Networking.UnityWebRequest.Replies=new Queue<string>(replies);
  EditResult result=null; int callbacks=0;var api=new EditApi("http://test",null);
  Action<EditResult> cb=delegate(EditResult r){result=r;callbacks++;};
  if(mode==0)api.TradeInfo(42,cb);else if(mode==1)api.TradeSet(42,123,50,1,1,cb);else api.TradeAddStock(42,123,9,cb);
  Check(callbacks==1,"one trade callback");return result;
 }
 static int Count { get { return UnityEngine.Networking.UnityWebRequest.Calls.Count; } }
 static void Main() {
  var r=Run("{\"ok\":true,\"data\":{\"ok\":true,\"entity\":{\"id\":4810}}}");
  Check(r.Ok && r.Ent.Id==4810 && Count==1,"new route");
  r=Run(Missing,"{\"ok\":true,\"text\":\"{\\\"ok\\\":true,\\\"entity\\\":{\\\"id\\\":4810}}\"}");
  Check(r.Ok && r.Ent.Id==4810 && Count==2,"legacy success");
  Check(UnityEngine.Networking.UnityWebRequest.Calls[1].Contains("/api/command") && UnityEngine.Networking.UnityWebRequest.Calls[1].Contains("edit linkteleporter 4810 -596.447 275.657 240.869"),"exact command");
  r=Run(Missing,"{\"ok\":false,\"exception\":\"Unknown command\"}");
  Check(!r.Ok && r.Err.Contains("PrefabEditorCore.dll"),"old core explanation");
  r=Run(Missing,"{\"ok\":true,\"text\":\"{\\\"ok\\\":false,\\\"err\\\":\\\"Not a teleporter\\\"}\"}");
  Check(!r.Ok && r.Err=="Not a teleporter","game rejection preserved");
  r=Run("HTTP:{\"error\":\"timeout\"}"); Check(!r.Ok && Count==1 && r.Err=="timeout","no retry for ambiguous failures");
  r=Run(Missing,"{\"ok\":true,\"text\":\"done\"}"); Check(!r.Ok,"no false success");
  r=Run(Missing,"HTTP:{\"error\":\"offline\"}"); Check(!r.Ok && r.Err=="offline","preserve fallback transport failure");
  r=Run(Missing,"{\"ok\":true,\"structured\":{\"ok\":true,\"entity\":{\"id\":4810}}}"); Check(r.Ok,"structured reply");
  int before=Count;
  new EditApi("http://test",null).LinkTeleporter(4810,float.NaN,0,0,delegate(EditResult value){r=value;});
  Check(!r.Ok && Count==before,"invalid coordinates never sent");
  string tradeReply="{\"ok\":true,\"structured\":{\"ok\":true,\"entity\":{\"id\":42},\"trade\":{\"stock\":0,\"itemHash\":0}}}";
  string[] routes={"trade-info","trade-set","trade-add-stock"};
  string[] commands={"edit tradeinfo 42","edit tradeset 42 123 50 1 1","edit tradeaddstock 42 123 9"};
  for(int mode=0;mode<3;mode++){
   string missing="HTTP:{\"error\":\"no such route: POST /api/edit/"+routes[mode]+"\"}";
   r=Trade(mode,missing,tradeReply);
   Check(r.Ok && Count==2 && Json.Value(Json.Value(r.Raw,"data"),"trade")!=null,"empty trade deck reply preserved");
   Check(UnityEngine.Networking.UnityWebRequest.Calls[1].Contains(commands[mode]),"exact legacy trade command");
   r=Trade(mode,"HTTP:{\"error\":\"timeout\"}");Check(!r.Ok&&Count==1,"trade timeout never retried");
   r=Trade(mode,missing,"{\"ok\":false,\"exception\":\"unknown command\"}");Check(!r.Ok&&r.Err.Contains("PrefabEditorCore.dll"),"old core guidance");
  }
  r=Trade(0,"{\"ok\":true,\"data\":{\"ok\":true,\"entity\":{\"id\":42},\"trade\":{\"stock\":0}}}");Check(r.Ok&&Count==1,"new trade route");
  r=EditApi.ParseTradeCommand(EditResult.Parse("{\"ok\":true,\"structured\":{\"ok\":true,\"entity\":{\"id\":42}}}"));Check(!r.Ok,"no success without trade data");
  r=EditApi.ParseTradeCommand(EditResult.Parse("{\"ok\":true,\"structured\":{\"ok\":false,\"err\":\"stock capacity\"}}"));Check(!r.Ok&&r.Err=="stock capacity","trade rejection preserved");
  Console.WriteLine("PASS: teleporter compatibility, exact commands, callback count, rejection, transport errors and false-success prevention.");
 }
}
'''
with tempfile.TemporaryDirectory(prefix='prefab-teleporter-test-') as folder:
    root=Path(folder)
    (root/'Test.cs').write_text(harness)
    exe=root/'Test.exe'
    subprocess.run([r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe','/nologo','/out:'+str(exe),str(source),str(root/'Test.cs')],check=True)
    subprocess.run([str(exe)],check=True)
