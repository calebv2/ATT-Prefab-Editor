"""Windows preference/launch regression checks using Tavern source and simulated console."""
import argparse
import sys
from pathlib import Path
import tempfile
import tkinter as tk
from types import SimpleNamespace
parser = argparse.ArgumentParser()
parser.add_argument('tavern', type=Path)
parser.add_argument('deps', type=Path)
args = parser.parse_args()
sys.path[:0] = [str(args.deps), str(args.tavern)]
import addons
addons.__path__.append(str(Path(__file__).resolve().parents[1] / 'tavern-addon'))
import addons.prefab_editor.client as addon
from addons.prefab_editor.settings import Settings
from addons.prefab_editor.invite import make_code, read_code

with tempfile.TemporaryDirectory() as temp:
    settings = Settings(temp)
    addon._preferences = settings
    settings.save('127.0.0.1', 'test-secret', True)
    assert 'test-secret' not in settings.path.read_text()
    assert settings.token(settings.read()) == 'test-secret'
    root = tk.Tk(); root.withdraw()
    win = addon.PrefabulatorWindow(root); win.withdraw()
    assert win.v_host.get() == '127.0.0.1'
    assert win.v_remember.get() and win.v_token.get() == 'test-secret'
    win.v_remember.set(False); win._remember_changed()
    assert settings.read() == {'host': '127.0.0.1'}
    win.v_host.set('192.0.2.1')
    assert not win.v_token.get()
    assert settings.read()['host'] == '192.0.2.1'
    code = make_code('example.test', 'pfa1.test-personal-secret')
    assert read_code(code) == ('example.test', 'pfa1.test-personal-secret')
    native_connect = addon.TavernKeeperWindow._attempt_connect
    calls = []
    addon.TavernKeeperWindow._attempt_connect = lambda self: calls.append((self.v_host.get(), self.v_token.get()))
    win.v_token.set(code)
    win.v_remember.set(True)
    win._remember_changed()
    assert settings.read()['host'] == 'example.test'
    assert settings.token(settings.read()) == 'pfa1.test-personal-secret'
    win._attempt_connect()
    assert calls == [('example.test', 'pfa1.test-personal-secret')]
    assert settings.read()['host'] == 'example.test'
    win.v_token.set('pfac1.invalid')
    win._attempt_connect()
    assert len(calls) == 1 and 'Invalid moderator code' in win._connect_status.get()
    addon.TavernKeeperWindow._attempt_connect = native_connect
    win.destroy(); root.destroy()

    game = Path(temp) / 'game'; (game / 'Mods').mkdir(parents=True)
    (game / 'Mods/PrefabEditor.dll').write_bytes(b'test')
    events = []
    class Client:
        def connect(self, host, token, **kwargs):
            assert host == '127.0.0.1' and token == 'test-secret'
            events.append('console'); self._connected = True
            return True, ''
        def disconnect(self): self._connected = False
    class Bridge:
        def __init__(self, client, *args, **kwargs):
            self.transport = SimpleNamespace(client=client)
            self.closed = False
            self.process = SimpleNamespace(poll=lambda: None)
        def start(self): events.append('bridge')
        def close(self): self.closed = True
    addon.WsConsoleClient = Client; addon.Bridge = Bridge
    launcher = SimpleNamespace(v_ip=SimpleNamespace(get=lambda: 'localhost'),
        v_exe=SimpleNamespace(get=lambda: str(game / 'game.exe')), _print=lambda _: None)
    settings.save('127.0.0.1', 'test-secret', True)
    addon._original_do_launch = lambda *a, **k: events.append('game')
    addon._do_launch(launcher, 'password')
    assert events == ['console', 'bridge', 'game'], events
    addon._do_launch(launcher, 'password')
    assert events == ['console', 'bridge', 'game', 'game']
    launcher.v_ip = SimpleNamespace(get=lambda: '192.0.2.1')
    addon._do_launch(launcher, 'password')
    assert addon._active.closed and events[-1] == 'game'
    assert events.count('console') == 1
    settings.save('127.0.0.1')
    launcher.v_ip = SimpleNamespace(get=lambda: '127.0.0.1')
    addon._do_launch(launcher, 'password')
    assert events.count('console') == 1
print('PASS: encrypted token, opt-out removal, address restore, credential isolation, localhost auto-connect, startup order, session reuse')
