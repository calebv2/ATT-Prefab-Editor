"""Compile and exercise actual server bridge commands against real JSON/files."""
from pathlib import Path
import io
import subprocess
import tempfile
import urllib.request
import zipfile

source = Path(__file__).resolve().parents[1]/'server/server-kit/core/Modules/AdminAccess.cs'
harness = r'''
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PrefabEditor.Modules;
namespace Fleck {
 public interface IWebSocketConnection {Action<string> OnMessage{get;set;} void Send(string s);void Close();}
 public class Socket:IWebSocketConnection {public Action<string> OnMessage{get;set;} public List<string> Sent=new List<string>();public bool Closed;public void Send(string s){Sent.Add(s);}public void Close(){Closed=true;}}
}
namespace Alta.Api.DataTransferModels.Models.Responses {
 public enum GroupPermissions {Console}
 public class UserInfoWithPermissions {public UserInfoWithPermissions(int id,string name,bool verified,IEnumerable<string> policies){}}
}
namespace Alta.Console {
 public class CommandContext {public CommandContext(Alta.Api.DataTransferModels.Models.Responses.UserInfoWithPermissions user,IEnumerable<Alta.Api.DataTransferModels.Models.Responses.GroupPermissions> permissions){}}
 public class CommandResult {public string ResultString="done";}
 public static class CommandService {public static List<string> Calls=new List<string>();public static Task<CommandResult> Handle(string command,CommandContext context){Calls.Add(command);return Task.FromResult(new CommandResult());}}
 public class WebSocketCommandHandler {
  public Fleck.IWebSocketConnection connection;public bool IsAuthorized;public WebSocketCommandHandler(Fleck.IWebSocketConnection s){connection=s;}
  public void OnOpen(){} public void OnClose(){} public void HandleRequest(string wire){}
  public enum ResponseType{A,B,CommandResult}
  public void Send(ResponseType type,object result,string text,uint? id){connection.Send(JsonConvert.SerializeObject(new{type="CommandResult",commandId=id,data=result}));}
 }
}
namespace Alta.Core.Threading {public static class MainThreadDispatcher {public static List<Action> Queue=new List<Action>();public static void Run(Action a){Queue.Add(a);}public static void Drain(){while(Queue.Count>0){var a=Queue[0];Queue.RemoveAt(0);a();}}}}
namespace HarmonyLib {
 public class Harmony {public Harmony(string id){}public void Patch(MethodInfo m,HarmonyMethod prefix=null,HarmonyMethod postfix=null){}public void UnpatchSelf(){}}
 public class HarmonyMethod {public int priority;public HarmonyMethod(Type t,string name){}}
 public static class Priority {public const int Last=0;}
 public static class AccessTools {public static MethodInfo Method(Type t,string name){return t.GetMethod(name);}public static FieldInfo Field(Type t,string name){return t.GetField(name);}}
}
class Test {
 static void Check(bool value,string message){if(!value)throw new Exception(message);}
 static object Call(string name,params object[] args){return typeof(AdminAccess).GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,args);}
 static string Access(object value){return "edit access "+Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value)));}
 static string Wire(string command){return JsonConvert.SerializeObject(new{id=3,content=command});}
 static JObject Last(Fleck.Socket socket){return JObject.Parse(socket.Sent.Last());}
 static JObject Result(Fleck.Socket socket){return JObject.Parse((string)Last(socket)["data"]["ResultString"]);}
 static JObject Owner(Alta.Console.WebSocketCommandHandler owner,Fleck.Socket socket,object action){Check(!(bool)Call("OwnerRequest",owner,Wire(Access(action))),"owner RPC intercepted");Alta.Core.Threading.MainThreadDispatcher.Drain();return Result(socket);}
 static void Main(string[] args){
  Directory.CreateDirectory(args[0]);AdminAccess.Initialize(args[0],s=>{throw new Exception(s);});
  var ownerSocket=new Fleck.Socket();var owner=new Alta.Console.WebSocketCommandHandler(ownerSocket){IsAuthorized=true};
  var created=Owner(owner,ownerSocket,new{action="create",name="Alice",tabs=new[]{"build"}});
  string token=(string)created["token"],id=(string)created["id"];
  Check(token.StartsWith("pfa1."),"personal token issued");
  string file=Path.Combine(args[0],"admin-access.json");Check(!File.ReadAllText(file).Contains(token),"plaintext token not persisted");
  var reload=new AdminAccessStore(file);Check(reload.Authenticate(token)!=null,"token survives reload");
  Check(reload.Authenticate(token+"x")==null,"modified token refused");
  var socket=new Fleck.Socket();int nativeCalls=0;socket.OnMessage=s=>nativeCalls++;
  var handler=new Alta.Console.WebSocketCommandHandler(socket);Call("Opened",handler);socket.OnMessage(token);
  Check(((string)Last(socket)["data"]).Contains("Connection Succeeded"),"personal handshake");
  Check(!handler.IsAuthorized && nativeCalls==0,"personal token never grants native owner auth");
  socket.OnMessage(Wire("edit spawnat 0 1 2 123"));Alta.Core.Threading.MainThreadDispatcher.Drain();
  Check(Alta.Console.CommandService.Calls.Count==1,"permitted build executes");
  foreach(string denied in new[]{"edit kill Alice","settings changesetting server XPX 2","save full-wipe","repeat 1 1 true","edit spawnat 0 1 2 123; save full-wipe",Access(new{action="create",name="Mallory",tabs=AdminAccessStore.TabNames})}){
   socket.OnMessage(Wire(denied));Alta.Core.Threading.MainThreadDispatcher.Drain();Check(Last(socket)["data"]["Exception"].Type!=JTokenType.Null,"forbidden command returns error");
  }
  Check(Alta.Console.CommandService.Calls.Count==1,"forbidden commands never dispatched");
  Owner(owner,ownerSocket,new{action="update",id=id,name="Alice",tabs=new[]{"trading"}});
  socket.OnMessage(Wire("edit spawnat 0 1 2 123"));Alta.Core.Threading.MainThreadDispatcher.Drain();
  Check(Alta.Console.CommandService.Calls.Count==1,"permission removal affects existing session");
  socket.OnMessage(Wire("edit tradeset 1 2 3 4 5"));Alta.Core.Threading.MainThreadDispatcher.Drain();Check(Alta.Console.CommandService.Calls.Count==2,"new permission affects existing session");
  socket.OnMessage(Wire(Access(new{action="self"})));Alta.Core.Threading.MainThreadDispatcher.Drain();Check((bool)Result(socket)["owner"]==false && (string)Result(socket)["tabs"][0]=="trading","self reflects current permissions");
  var rotated=Owner(owner,ownerSocket,new{action="rotate",id=id});Check(socket.Closed,"rotation closes live session");
  reload=new AdminAccessStore(file);Check(reload.Authenticate(token)==null && reload.Authenticate((string)rotated["token"])!=null,"rotation invalidates only old token");
  var bob=Owner(owner,ownerSocket,new{action="create",name="Bob",tabs=AdminAccessStore.TabNames});
  var bobSocket=new Fleck.Socket();var bobHandler=new Alta.Console.WebSocketCommandHandler(bobSocket);Call("Opened",bobHandler);bobSocket.OnMessage((string)bob["token"]);
  bobSocket.OnMessage(Wire(Access(new{action="revoke",id=id})));Alta.Core.Threading.MainThreadDispatcher.Drain();Check(Last(bobSocket)["data"]["Exception"].Type!=JTokenType.Null,"all tabs cannot manage access");
  Owner(owner,ownerSocket,new{action="revoke",id=(string)bob["id"]});Check(bobSocket.Closed,"revoke closes live session");
  bobSocket.OnMessage(Wire("edit kill Alice"));Alta.Core.Threading.MainThreadDispatcher.Drain();Check(Alta.Console.CommandService.Calls.Count==2,"revoked session cannot execute queued commands");
  var invalidSocket=new Fleck.Socket();var invalidHandler=new Alta.Console.WebSocketCommandHandler(invalidSocket);Call("Opened",invalidHandler);invalidSocket.OnMessage((string)bob["token"]);Check(invalidSocket.Closed,"revoked token cannot reconnect");
  var anonymous=new Alta.Console.WebSocketCommandHandler(new Fleck.Socket());Check(!(bool)Call("OwnerRequest",anonymous,Wire(Access(new{action="list"}))),"unauthenticated management blocked");
  // Console is separately granted, persisted, and enforced on the server.
  var ownerSelf=Owner(owner,ownerSocket,new{action="self"});
  Check(((JArray)ownerSelf["tabs"]).Any(t=>(string)t=="console"),"owner sees console tab");
  var consoleAdmin=Owner(owner,ownerSocket,new{action="create",name="Console admin",tabs=new[]{"console"}});
  string consoleId=(string)consoleAdmin["id"],consoleToken=(string)consoleAdmin["token"];
  reload=new AdminAccessStore(file);
  Check(reload.Authenticate(consoleToken).Tabs.SequenceEqual(new[]{"console"}),"console grant persists and reloads");
  var consoleSocket=new Fleck.Socket();var consoleHandler=new Alta.Console.WebSocketCommandHandler(consoleSocket);
  Call("Opened",consoleHandler);consoleSocket.OnMessage(consoleToken);
  Check(!consoleHandler.IsAuthorized,"console tab does not grant owner authentication");
  int consoleBefore=Alta.Console.CommandService.Calls.Count;
  foreach(string command in new[]{"server status","time set 12","settings changesetting server XPX 2"}){
   consoleSocket.OnMessage(Wire(command));Alta.Core.Threading.MainThreadDispatcher.Drain();
  }
  Check(Alta.Console.CommandService.Calls.Count==consoleBefore+3,"console-only profile dispatches native commands once");
  foreach(string denied in new[]{"edit access", " EDIT\tACCESS anything", "edit  access anything", "EdIt AcCeSs anything", "edit\vaccess anything", "edit\u00a0access anything", "server status; quit", "server status\nquit",Access(new{action="create",name="Escalation",tabs=AdminAccessStore.TabNames})}){
   consoleSocket.OnMessage(Wire(denied));Alta.Core.Threading.MainThreadDispatcher.Drain();
   Check(Last(consoleSocket)["data"]["Exception"].Type!=JTokenType.Null,"console cannot manage access or chain commands");
  }
  Check(Alta.Console.CommandService.Calls.Count==consoleBefore+3,"access prefix variants never reach native parser");
  consoleSocket.OnMessage(Wire(Access(new{action="self"})));Alta.Core.Threading.MainThreadDispatcher.Drain();
  Check((bool)Result(consoleSocket)["owner"]==false && (string)Result(consoleSocket)["tabs"][0]=="console","console self remains a personal profile");
  // Change persisted authorization before the already queued command runs.
  var liveStore=(AdminAccessStore)typeof(AdminAccess).GetField("store",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
  consoleSocket.OnMessage(Wire("server status"));
  liveStore.Change(JObject.FromObject(new{action="update",id=consoleId,name="Console admin",tabs=new string[0]}));
  Alta.Core.Threading.MainThreadDispatcher.Drain();
  Check(Alta.Console.CommandService.Calls.Count==consoleBefore+3 && Last(consoleSocket)["data"]["Exception"].Type!=JTokenType.Null,"removing console blocks an already queued command");
  liveStore.Change(JObject.FromObject(new{action="update",id=consoleId,name="Console admin",tabs=new[]{"console"}}));
  consoleSocket.OnMessage(Wire("server status"));
  liveStore.Change(JObject.FromObject(new{action="revoke",id=consoleId}));
  Call("CloseInvalidSessions");Alta.Core.Threading.MainThreadDispatcher.Drain();
  Check(consoleSocket.Closed && Alta.Console.CommandService.Calls.Count==consoleBefore+3,"console revocation closes session and rejects queued command");
  var consoleRetrySocket=new Fleck.Socket();var consoleRetryHandler=new Alta.Console.WebSocketCommandHandler(consoleRetrySocket);Call("Opened",consoleRetryHandler);consoleRetrySocket.OnMessage(consoleToken);
  Check(consoleRetrySocket.Closed,"revoked console credential cannot reconnect");
  foreach(string command in new[]{"exec","edit access anything","settings changesetting server IsSaving false","edit undo","spawn exact 0,0,0 0,0,0 1 | quit"})Check(!AdminAccessStore.Allowed(command,new[]{"build","settings"}),"allowlist default denial");
  File.WriteAllText(file,"broken");Check(new AdminAccessStore(file).Authenticate((string)rotated["token"])==null,"corrupt file fails closed");
  AdminAccess.Shutdown();System.Console.WriteLine("PASS: personal admission, hashed storage/reload, owner-only management, tab restrictions, active changes, rotation, revocation/disconnect, reconnect denial, corruption, console grants/removal, queued revocation and raw-command bypass rejection");
 }
}
'''
with tempfile.TemporaryDirectory(prefix='tavern-server-test-') as folder:
    root=Path(folder)
    package=urllib.request.urlopen('https://api.nuget.org/v3-flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg', timeout=30).read()
    with zipfile.ZipFile(io.BytesIO(package)) as archive:
        (root/'Newtonsoft.Json.dll').write_bytes(archive.read('lib/net45/Newtonsoft.Json.dll'))
    (root/'Test.cs').write_text(harness)
    subprocess.run([r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe','/nologo','/out:'+str(root/'test.exe'),'/r:'+str(root/'Newtonsoft.Json.dll'),str(source),str(source.with_name('AdminAccessStore.cs')),str(root/'Test.cs')],check=True)
    subprocess.run([str(root/'test.exe'),str(root/'data')],check=True)
