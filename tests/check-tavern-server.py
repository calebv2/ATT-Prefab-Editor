"""Compile and exercise actual server bridge commands against real JSON/files."""
from pathlib import Path
import io
import subprocess
import tempfile
import urllib.request
import zipfile

source = Path(__file__).resolve().parents[1]/'server/server-kit/core/Modules/TavernBridge.cs'
harness = r'''
using System;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using PrefabEditor.Modules;
namespace Alta.Console {public class CommandAttribute:Attribute {public CommandAttribute(string a,string b){}}}
namespace PrefabEditor.Modules {public static class EditJson {public static string Err(string error){return new JObject(new JProperty("ok",false),new JProperty("error",error)).ToString();}}}
class Test {
 static void Check(bool value,string message){if(!value)throw new Exception(message);}
 static bool Set(string body){return (bool)JObject.Parse(EditorAdminModule.InfoBoardSet(Convert.ToBase64String(Encoding.UTF8.GetBytes(body))))["ok"];}
 static void Main(string[] args){
  Directory.CreateDirectory(args[0]);EditorAdminModule.InitializeBridge(args[0]);
  Check((int)JObject.Parse(EditorAdminModule.Capabilities())["tavernBridge"]==1,"capability handshake");
  Check(!(bool)JObject.Parse(EditorAdminModule.InfoBoards())["configured"],"absent plugin distinguished");
  string file=Path.Combine(args[0],"InfoBoards.json");
  string original="{\"other\":42,\"boards\":{\"1.0,2.0,3.0\":{\"channel\":\"town\",\"text\":\"before\"}}}";
  File.WriteAllText(file,original);
  Check(!Set("{\"key\":\"1.0,2.0,3.0\",\"text\":\"bad\"}") && File.ReadAllText(file)==original,"missing rotation must not modify server file");
  Check(!Set("{\"key\":\"9.0,2.0,3.0\",\"text\":\"bad\",\"rotation\":null}"),"unregistered board rejected");
  Check(Set("{\"key\":\"1.0,2.0,3.0\",\"text\":\"after\",\"rotation\":{\"enabled\":true,\"interval\":3,\"unit\":\"seconds\",\"messages\":[\"one\",\"two\"]}}"),"valid rotation saves");
  JObject doc=JObject.Parse(File.ReadAllText(file));Check((int)doc["other"]==42,"unrelated settings retained");
  Check((string)doc["boards"]["1.0,2.0,3.0"]["text"]=="after","text updated server-side");
  Check(File.ReadAllText(file+".prefab-backup")==original,"previous version backed up");
  Check(Set("{\"key\":\"1.0,2.0,3.0\",\"text\":\"final\",\"rotation\":null}"),"repeat replacement works");
  Check(JObject.Parse(File.ReadAllText(file))["boards"]["1.0,2.0,3.0"]["rotation"]==null,"rotation removed");
  Check(((JArray)JObject.Parse(EditorAdminModule.InfoBoards())["boards"]).Count==1,"read response matches panel contract");
  File.WriteAllText(file,"invalid");Check(!Set("{\"key\":\"1.0,2.0,3.0\",\"text\":\"lost\",\"rotation\":null}") && File.ReadAllText(file)=="invalid","invalid existing file preserved");
  Console.WriteLine("PASS: server capability, missing plugin, field validation, server-side board read/write, backups, repeat writes and corrupt-file preservation.");
 }
}
'''
with tempfile.TemporaryDirectory(prefix='tavern-server-test-') as folder:
    root=Path(folder)
    package=urllib.request.urlopen('https://api.nuget.org/v3-flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg', timeout=30).read()
    with zipfile.ZipFile(io.BytesIO(package)) as archive:
        (root/'Newtonsoft.Json.dll').write_bytes(archive.read('lib/net45/Newtonsoft.Json.dll'))
    (root/'Test.cs').write_text(harness)
    subprocess.run([r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe','/nologo','/out:'+str(root/'test.exe'),'/r:'+str(root/'Newtonsoft.Json.dll'),str(source),str(root/'Test.cs')],check=True)
    subprocess.run([str(root/'test.exe'),str(root/'data')],check=True)
