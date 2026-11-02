"""Check shared-folder startup roles and real installer mode/updates."""
from pathlib import Path
import importlib.util,json,re,subprocess,tempfile
repo=Path(__file__).resolve().parents[1]
source=(repo/'server/server-kit/core/Entry.cs').read_text(encoding='utf-8')
def method(name):
    match=re.search(r'^        (?:public override )?void '+name+r'\(',source,re.M);start=match.start();opening=source.index('{',start);depth=0
    for token in re.finditer(r'//[^\n]*|/\*[\s\S]*?\*/|"(?:\\.|[^"\\])*"|[{}]',source[opening:]):
        if token.group()=='{':depth+=1
        elif token.group()=='}':
            depth-=1
            if depth==0:return source[start:opening+token.end()]
    raise AssertionError(name)
harness=r'''
using System;
namespace UnityEngine {public static class Application{public static bool isBatchMode;}}
public static class NetworkSceneManager {public static bool IsServer;}
class Base {public virtual void OnUpdate(){}}
class Log {public void Msg(string s){}}
class Dispatcher {public int Drains;public void Drain(int n){Drains++;}}
class Context {public Dispatcher Dispatcher=new Dispatcher();}
class TavernModule {public bool Initialized=true;public int Updates;public void OnUpdate(){Updates++;}}
class Entry:Base {
 bool _up,_waitingForServer,_bootAttempted;public int Boots;Log LoggerInstance=new Log();Context _ctx=new Context();TavernModule[] _modules={new TavernModule()};
 void Boot(){Boots++;_up=true;}
METHODS
 static void Check(bool b,string s){if(!b)throw new Exception(s);}
 static void Main(){
  var client=new Entry();client.TryBoot();client.OnUpdate();Check(client.Boots==0 && client._ctx.Dispatcher.Drains==0,"shared-folder player must not boot server listener/auth/services");
  NetworkSceneManager.IsServer=true;client.OnUpdate();client.TryBoot();client.OnUpdate();Check(client.Boots==1 && client._ctx.Dispatcher.Drains==2,"interactive host starts exactly once when hosting begins");
  NetworkSceneManager.IsServer=false;UnityEngine.Application.isBatchMode=true;var dedicated=new Entry();dedicated.TryBoot();dedicated.TryBoot();dedicated.OnUpdate();Check(dedicated.Boots==1 && dedicated._modules[0].Updates==1,"dedicated server retains late-start boot without waiting for scene");
  Console.WriteLine("PASS: idle player, deferred local host and dedicated server roles; no duplicate boot.");
 }
}
'''.replace('METHODS',method('TryBoot')+'\n'+method('OnUpdate'))
with tempfile.TemporaryDirectory(prefix='prefab-shared-') as folder:
    root=Path(folder);(root/'Test.cs').write_text(harness)
    subprocess.run([r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe','/nologo','/out:'+str(root/'test.exe'),str(root/'Test.cs')],check=True)
    subprocess.run([str(root/'test.exe')],check=True)
    spec=importlib.util.spec_from_file_location('shared_runtime',repo/'tavern-addon/prefab_editor/runtime.py')
    runtime=importlib.util.module_from_spec(spec);spec.loader.exec_module(runtime)
    runtime.ROOT=root/'addon';payload=runtime.ROOT/'payload';payload.mkdir(parents=True)
    for name in ('PrefabEditor.dll','PrefabEditorCore.dll','PrefabEditorCatalog.json'):(payload/name).write_bytes(('new '+name).encode())
    (payload/'PrefabEditorImages').mkdir();(payload/'PrefabEditorImages/1.png').write_bytes(b'image')
    game=root/'game';(game/'A Township Tale_Data').mkdir(parents=True);(game/'MelonLoader').mkdir();(game/'Mods').mkdir();(game/'Mods/PrefabEditorCore.dll').write_bytes(b'old core')
    manifest=runtime.ROOT/'author.json';manifest.write_text(json.dumps({'installation':'shared'}))
    runtime.install(game,'client')
    assert (game/'Mods/PrefabEditorCore.dll').read_bytes()==(payload/'PrefabEditorCore.dll').read_bytes()
    assert (game/'Mods/PrefabEditor.dll').read_bytes()==(payload/'PrefabEditor.dll').read_bytes()
    assert (game/'Mods/PrefabEditorImages/1.png').read_bytes()==b'image'
    assert len(list((game/'Mods').glob('PrefabEditorCore.dll.before-tavern-*')))==1
    manifest.write_text('{}');(game/'Mods/PrefabEditorCore.dll').write_bytes(b'keep existing core');runtime.install(game,'client')
    assert (game/'Mods/PrefabEditorCore.dll').read_bytes()==b'keep existing core','regular player package must ignore stale server payload'
    manifest.write_text(json.dumps({'installation':'shared'}));(payload/'PrefabEditorCore.dll').unlink();(payload/'PrefabEditor.dll').write_bytes(b'not yet installed')
    before=(game/'Mods/PrefabEditor.dll').read_bytes()
    try:runtime.install(game,'client');raise AssertionError('missing shared core accepted')
    except RuntimeError as e:assert 'Included mod files are missing' in str(e)
    assert (game/'Mods/PrefabEditor.dll').read_bytes()==before,'validate full shared payload before replacing files'
print('PASS: shared two-DLL install/update, images, backup, regular-player isolation and incomplete-payload preflight.')
