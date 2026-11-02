"""Exercise real helper leases, launch handoff, Retry and closed-session UI status."""
import ast,importlib.util,json,socket,tempfile,threading
from pathlib import Path
from types import SimpleNamespace
repo=Path(__file__).resolve().parents[1];source=repo/'tavern-addon/prefab_editor'
spec=importlib.util.spec_from_file_location('handoff_runtime',source/'runtime.py');runtime=importlib.util.module_from_spec(spec);spec.loader.exec_module(runtime)
tree=ast.parse((source/'client.py').read_text(encoding='utf-8'))
functions={n.name:n for n in tree.body if isinstance(n,ast.FunctionDef)}
class Client:
    def __init__(self):self._connected=True;self._handle=lambda raw:None;self._lock=threading.Lock()
class Process:
    def __init__(self):self.code=None
    def poll(self):return self.code
    def terminate(self):self.code=0
    def wait(self,**kw):return self.code
class Value:
    def __init__(self,v):self.v=v
    def get(self):return self.v
    def set(self,v):self.v=v

def hook(bridge,game,host):
    logs=[];ns={'Path':Path,'socket':socket,'_active':bridge,'_session_lock':threading.RLock(),'_preferences':SimpleNamespace(read=lambda:{})}
    exec(compile(ast.Module(body=[functions[n] for n in ['_same_server','_auto_connect']],type_ignores=[]),'launch-hook','exec'),ns)
    launcher=SimpleNamespace(v_ip=Value(host),v_exe=Value(str(game/'game.exe')),_print=logs.append)
    ns['_auto_connect'](launcher)
    return ns,logs

def bridge(game):
    b=runtime.Bridge(Client(),game,game,host='127.0.0.1',token='dummy-owner')
    b.host='127.0.0.1';b.process=Process();b.url='http://127.0.0.1:51000'
    b.written=json.dumps({'panelUrl':b.url,'managedBy':'TavernLauncher','sessionId':str(id(b))}).encode();b.ensure_connection();return b

with tempfile.TemporaryDirectory(prefix='prefab-handoff-') as folder:
    root=Path(folder);a=root/'configured game';b=root/'launched game'
    (a/'UserData').mkdir(parents=True);prior=b'{"panelUrl":"http://127.0.0.1:1766"}';initial=a/'UserData/PrefabEditorConnection.json';initial.write_bytes(prior)
    active=bridge(a);first=initial.read_bytes();initial.unlink();active.ensure_connection();assert initial.read_bytes()==first
    target=b/'UserData/PrefabEditorConnection.json'
    ns,logs=hook(active,b,'localhost');assert not active.closed and target.read_bytes()==first,'pre-launch must publish to actual launched folder without saved token'
    target.unlink();hook(active,b,'127.0.0.1');assert target.read_bytes()==first,'reuse must repair a removed launch descriptor'
    # Retry uses the existing live helper rather than claiming it is already running.
    (b/'Mods').mkdir();(b/'Mods/PrefabEditor.dll').write_bytes(b'installed');target.unlink()
    status=Value('');details=Value('');tracked=[None]
    env=dict(ns,status=status,details=details,tracked_bridge=tracked,game=Value(str(b)),window=SimpleNamespace(_connected=True,v_host=Value('127.0.0.1'),v_token=Value('dummy-owner')),background=lambda action,done:status.set(done(action())))
    nested={n.name:n for n in ast.walk(functions['build']) if isinstance(n,ast.FunctionDef)}
    exec(compile(ast.Module(body=[nested['connection_ready'],nested['start_editor']],type_ignores=[]),'retry','exec'),env);env['start_editor']()
    assert status.get().startswith('CONNECTED') and str(target) in details.get() and tracked[0] is active and target.exists()
    # UI must clear CONNECTED after the launch hook closes the helper for a mismatched host.
    _,logs=hook(active,b,'203.0.113.10');assert active.closed and initial.read_bytes()==prior and not target.exists()
    assert '127.0.0.1' in logs[0] and '203.0.113.10' in logs[0] and 'Change server / reconnect' in logs[0]
    env.update(busy=[False],window=SimpleNamespace(winfo_exists=lambda:False))
    exec(compile(ast.Module(body=[nested['monitor']],type_ignores=[]),'monitor','exec'),env);env['monitor']()
    assert not status.get().startswith('CONNECTED') and '203.0.113.10' in status.get() and details.get()==''
    active=bridge(a);active.connection.write_bytes(b'newer session');
    try:active.ensure_connection();raise AssertionError('newer connection was overwritten')
    except RuntimeError:pass
    active.close();assert active.connection.read_bytes()==b'newer session','shutdown cannot delete or restore over a different owner'
    active=bridge(a);active.process.terminate();active.connection.unlink()
    try:active.ensure_connection();raise AssertionError('stopped helper recreated a descriptor')
    except RuntimeError:pass
    active.close();assert not active.connection.exists()
print('PASS: missing-file repair, actual game handoff, manual Retry, host-mismatch explanation, stale CONNECTED cleanup, lease ownership and stopped-helper guard.')
