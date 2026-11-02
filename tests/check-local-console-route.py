"""Test explicit localhost console paired with a different game endpoint."""
import argparse,sys,tempfile,threading,tkinter as tk
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch
p=argparse.ArgumentParser();p.add_argument('tavern',type=Path);p.add_argument('deps',type=Path);p.add_argument('source',type=Path,nargs='?',default=Path(__file__).resolve().parents[1]/'tavern-addon/prefab_editor');args=p.parse_args()
sys.path[:0]=[str(args.deps.resolve()),str(args.tavern.resolve())]
import addons
addons.__path__.insert(0,str(args.source.resolve().parent))
from addons.prefab_editor import client as a
from addons.prefab_editor.settings import Settings
from addons.prefab_editor.invite import make_code
import client.core.tavernkeeper.window as keeper
with tempfile.TemporaryDirectory() as folder,patch.object(keeper,'_finish_dark_window',lambda w:None):
 root=tk.Tk();root.withdraw();root.v_ip=tk.StringVar(root,value='203.0.113.10');a._preferences=Settings(folder)
 def form():
  w=a.PrefabulatorWindow(root);w.withdraw();return w
 w=form();assert w.v_host.get()==root.v_ip.get() and not w.v_local_console.get()
 w.v_local_console.set(True);w._local_console_changed();assert w.v_host.get()=='127.0.0.1' and root.v_ip.get()=='203.0.113.10'
 w.v_token.set('owner-local-secret');w.v_remember.set(True);w._remember_changed()
 saved=a._preferences.read();assert saved['gameHost']=='203.0.113.10' and saved['host']=='127.0.0.1' and a._preferences.token(saved)=='owner-local-secret'
 assert 'owner-local-secret' not in a._preferences.path.read_text()
 w._on_close();w=form();assert w.v_local_console.get() and w.v_token.get()=='owner-local-secret' and w.v_host.get()=='127.0.0.1'
 # Freeze a pending route even if the main launcher or entry changes before connection completes.
 with patch.object(a.TavernKeeperWindow,'_attempt_connect',lambda self:None):w._attempt_connect()
 assert w._connecting_route[:2]==('203.0.113.10','127.0.0.1')
 root.v_ip.set('203.0.113.20');assert w._game_host=='203.0.113.10' and w.v_host.get()=='127.0.0.1'
 w.v_token.set('edited-during-connect');w._connected=True
 with patch.object(a,'build',lambda parent,window:None):w._connect_succeeded()
 assert w.v_token.get()=='owner-local-secret' and w._game_host=='203.0.113.10'
 assert 'Server changed' in w._server_status.get()
 w._on_close()
 # No auto fallback or credential inheritance for another game server.
 w=form();assert not w.v_local_console.get() and w.v_host.get()=='203.0.113.20' and not w.v_token.get()
 w.v_local_console.set(True);w._local_console_changed();w.v_token.set('unsaved-secret')
 root.v_ip.set('203.0.113.30');assert not w.v_local_console.get() and w.v_host.get()=='203.0.113.30' and not w.v_token.get()
 # Invitation codes select their own console, disabling a prior local override.
 w.v_local_console.set(True);w._local_console_changed();w.v_token.set(make_code('moderator.example.test','pfa1.personal-test'))
 calls=[]
 with patch.object(a.TavernKeeperWindow,'_attempt_connect',lambda self:calls.append((self.v_host.get(),self.v_token.get()))):w._attempt_connect()
 assert calls==[('moderator.example.test','pfa1.personal-test')] and not w.v_local_console.get()
 w._on_close();root.destroy()
 # Both launch implementations reuse the live helper by game endpoint, not console endpoint.
 game=Path(folder)/'game';(game/'Mods').mkdir(parents=True);(game/'Mods/PrefabEditor.dll').write_bytes(b'mod')
 class Bridge:
  host='127.0.0.1';game_host='203.0.113.10';closed=False
  transport=SimpleNamespace(client=SimpleNamespace(_connected=True))
  process=SimpleNamespace(poll=lambda:None)
  def ensure_connection(self,path):self.target=path
  def close(self):self.closed=True
 b=Bridge();a._active=b;a._preferences.save('127.0.0.1','owner-local-secret',True,game_host=b.game_host)
 logs=[]
 def launch(host):
  if a._auto_connect.__code__.co_argcount==1:a._auto_connect(SimpleNamespace(v_ip=SimpleNamespace(get=lambda:host),v_exe=SimpleNamespace(get=lambda:str(game/'game.exe')),_print=logs.append))
  else:a._auto_connect(host,str(game/'game.exe'),logs.append)
 launch(b.game_host);assert not b.closed and b.target==game
 launch('203.0.113.99');assert b.closed and logs
 # Recreate a saved local route: console connects only to localhost, never the public game address.
 a._active=None;events=[]
 class Client:
  def __init__(self):self._connected=False
  def connect(self,host,token,**kw):events.append((host,token));self._connected=True;return True,''
  def disconnect(self):self._connected=False
 class NewBridge:
  def __init__(self,client,*args,**kw):self.closed=False;self.transport=SimpleNamespace(client=client)
  def start(self):events.append('started')
  def close(self):self.closed=True
 with patch.object(a,'WsConsoleClient',Client),patch.object(a,'Bridge',NewBridge),patch.object(a.atexit,'register',lambda *args:None),patch.object(a,'ensure_runtime',lambda **kw:None,create=True):launch('203.0.113.10')
 assert events==[('127.0.0.1','owner-local-secret'),'started'] and a._active.game_host=='203.0.113.10'
 a._active.close();a._active=None
print('PASS: explicit local route, encrypted saved pairing, form restore, pending connection freeze, different-server credential isolation, moderator-code routing, launch reuse and saved localhost reconnect.')
