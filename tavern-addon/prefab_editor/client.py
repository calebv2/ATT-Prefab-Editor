"""Prefabulator connects the in-game editor using Tavern's console transport."""
import threading
import atexit
import socket
from pathlib import Path
import tkinter as tk
from tkinter import filedialog, messagebox
from client.core.config import load_cfg
from client.core.tavernkeeper.window import TavernKeeperWindow
from client.core.launcher_window import ClientLauncher
from tavern_shared.paths import _tavern_data_dir
from tavern_shared.theme import BG, PARCH, MUTED, _btn
from .runtime import Bridge, install, shared_install, SessionTransport
from .invite import PREFIX, read_code
from .settings import Settings
from .access import AccessWindow, request as access_request
from tavern_shared.ws_console_client import WsConsoleClient

_active = None
_session_lock = threading.RLock()
_preferences = Settings(Path(_tavern_data_dir()) / "prefab_editor")


def build(parent, window):
    global _active
    cfg = load_cfg()
    shared = shared_install()
    game = tk.StringVar(value=str(Path(cfg['game_exe']).parent) if cfg.get('game_exe') else '')
    status = tk.StringVar(value='Console connected. Starting the in-game editor connection...')
    tk.Label(parent, text='Prefabulator', bg=BG, fg=PARCH, font=('Segoe UI', 14, 'bold')).pack(anchor='w', padx=12, pady=12)
    install_hint = ('Shared Mods setup: stop BOTH the server and game before updating both included mods.\n'
                    if shared else 'First time: close the game and install the included player mod below.\n')
    tk.Label(parent, text='Prefabulator connects your in-game editor. All editing happens in the game.\n' + install_hint + 'Then launch/join this server normally and press F1.\nKeep Prefabulator open; minimizing it is fine.',
             bg=BG, fg=MUTED, justify='left', wraplength=680).pack(anchor='w', padx=12)
    tk.Entry(parent, textvariable=game, width=80).pack(fill='x', padx=12, pady=8)
    _btn(parent, 'Choose game folder', lambda: choose(), 'primary').pack(anchor='w', padx=12)
    tk.Label(parent, textvariable=status, bg=BG, fg=PARCH, justify='left', wraplength=680).pack(anchor='w', padx=12, pady=12)
    busy = [False]
    tracked_bridge = [None]
    details = tk.StringVar(value='')
    tk.Label(parent, textvariable=details, bg=BG, fg=MUTED, justify='left', wraplength=680).pack(anchor='w', padx=12, pady=4)

    def choose():
        path = filedialog.askdirectory(parent=window, title='Choose the game folder containing A Township Tale_Data')
        if path:
            game.set(path)

    def background(action, done):
        if busy[0]:
            return
        busy[0] = True
        status.set('Please wait...')
        def work():
            try:
                result = action()
                window.after(0, lambda: finish(None, result))
            except Exception as error:
                window.after(0, lambda e=str(error): finish(e, None))
        def finish(error, result):
            busy[0] = False
            status.set(error if error else done(result))
        threading.Thread(target=work, daemon=True).start()

    def install_player():
        path = game.get().strip()
        if not path:
            choose()
            path = game.get().strip()
        if path:
            def installed(_):
                if not shared:
                    window.after(0, start_editor)
                return ('Server and player mods installed in the shared Mods folder. Start the server, reconnect Prefabulator, then launch the game.'
                        if shared else 'Player mod installed. Connecting the editor...')
            background(lambda: install(path, 'client'), installed)

    def connection_ready(bridge, path):
        target = Path(path).resolve() / 'UserData' / 'PrefabEditorConnection.json'
        details.set('Local connection file: ' + str(target) + '\nLocal editor: ' + bridge.url)
        return 'CONNECTED. If the game is already open, close and relaunch the player game. Keep Prefabulator open.'

    def start_editor():
        path = game.get().strip()
        if not (Path(path) / 'Mods' / 'PrefabEditor.dll').is_file():
            status.set('Install the player mod first using the button above.')
            return
        if not window._connected:
            status.set('Close Prefabulator and reopen it to reconnect to your server.')
            return
        connection_host = window.v_host.get().strip() or '127.0.0.1'
        connection_token = window.v_token.get().strip()
        def connect_editor():
            global _active
            with _session_lock:
                if _active and not _active.closed:
                    if not _same_server(getattr(_active, 'host', ''), connection_host):
                        raise RuntimeError('Disconnect the existing editor before changing servers.')
                    _active.ensure_connection(path)
                    tracked_bridge[0] = _active
                    return _active
                bridge = Bridge(window._ws_client, path, Path(_tavern_data_dir()) / 'prefab_editor',
                                host=connection_host, token=connection_token,
                                access_transport=management_transport)
                bridge.host = connection_host
                _active = bridge
                tracked_bridge[0] = bridge
                try:
                    bridge.start()
                except Exception as error:
                    bridge.close_reason = 'Editor connection failed: ' + str(error)
                    bridge.close()
                    raise
                return bridge
        background(connect_editor, lambda bridge: connection_ready(bridge, path))

    def stop_editor():
        if _active and not _active.closed:
            background(_active.close, lambda _: 'Stopped. Restart the game after starting a different editor connection.')

    install_label = ('1. Update BOTH mods (server and game stopped)' if shared else '1. Install / update player mod (game closed)')
    _btn(parent, install_label, install_player, 'primary').pack(anchor='w', padx=12, pady=6)
    _btn(parent, 'Retry editor connection', start_editor, 'primary').pack(anchor='w', padx=12, pady=6)
    _btn(parent, 'Disconnect editor', stop_editor).pack(anchor='w', padx=12, pady=6)
    def forget_token():
        try:
            _preferences.save(_preferences.read().get('host', ''))
            status.set('Saved token removed. Enter it in Prefabulator next session. Your address is still saved.')
        except OSError:
            status.set('Could not remove saved token. Check your user settings folder permissions.')
    _btn(parent, 'Forget saved access token', forget_token).pack(anchor='w', padx=12, pady=6)
    # Access management is intercepted on WebSocket, not the native HTTP console.
    # Personal sessions already have a transport owned by their editor bridge.
    management_transport = (None if window.v_token.get().strip().startswith('pfa1.')
                            else SessionTransport(window._ws_client))
    owner_button = _btn(parent, 'Manage Admin Access (owner)', lambda: AccessWindow(
        window, management_transport, window.v_host.get().strip() or '127.0.0.1'))
    def check_owner():
        if management_transport is None:
            return
        def check():
            try:
                result = access_request(management_transport, 'self')
                window.after(0, lambda: access_ready(result))
            except Exception as error:
                window.after(0, lambda e=str(error): status.set(e))
        threading.Thread(target=check, daemon=True).start()
    def access_ready(result):
        if result.get('owner') is True:
            owner_button.pack(anchor='w', padx=12, pady=6)
    window.after(0, start_editor)
    window.after(500, check_owner)
    window.bind('<Destroy>', lambda event: management_transport.close() if event.widget is window and management_transport else None, add='+')

    # Toplevel destruction also happens on disconnect/closing the launcher.
    def destroyed(event):
        if event.widget is window and _active and _active.transport.client is window._ws_client:
            threading.Thread(target=_active.close, daemon=True).start()
    window.bind('<Destroy>', destroyed, add='+')

    def monitor():
        bridge = tracked_bridge[0]
        if bridge is not None and not busy[0]:
            if bridge.closed:
                details.set('')
                status.set(bridge.close_reason or 'Connection stopped. Click Retry editor connection, then relaunch the player game.')
                tracked_bridge[0] = None
            elif not bridge.transport.client._connected or (bridge.process and bridge.process.poll() is not None):
                bridge.close_reason = 'Connection stopped. Reconnect Prefabulator, then relaunch the player game.'
                background(bridge.close, lambda _: bridge.close_reason)
            else:
                try:
                    bridge.ensure_connection()
                except (OSError, RuntimeError) as error:
                    status.set('Connection file unavailable: ' + str(error))
        if window.winfo_exists():
            window.after(1500, monitor)
    window.after(1500, monitor)


class PrefabulatorWindow(TavernKeeperWindow):
    """Reuse only Tavern's connection form/auth; never build its editing tabs."""
    def _build_connect_form(self):
        super()._build_connect_form()
        self.title('Prefabulator')
        def relabel(widget):
            if isinstance(widget, tk.Label) and 'TavernKeeper' in str(widget.cget('text')):
                widget.configure(text='Prefabulator')
            if isinstance(widget, tk.Label) and str(widget.cget('text')) == 'CONSOLE TOKEN':
                widget.configure(text='MODERATOR CODE or owner console token')
            for child in widget.winfo_children():
                relabel(child)
        relabel(self)
        saved = _preferences.read()
        self.v_host.set(saved.get('host', ''))
        self.v_remember = tk.BooleanVar(value=bool(saved.get('protectedToken')))
        try:
            self.v_token.set(_preferences.token(saved))
        except Exception:
            self._connect_status.set('Saved token unavailable. Enter it again.')
        tk.Checkbutton(self, text='Save my access token and auto-connect when I launch this server',
                       variable=self.v_remember, command=self._remember_changed,
                       bg=BG, fg=PARCH, selectcolor=BG, wraplength=390).pack(fill='x', padx=20)
        tk.Label(self, text='Moderator: paste the code from your owner and click Connect; it fills in the server address.\nSaved tokens are encrypted for this Windows account.',
                 bg=BG, fg=MUTED, wraplength=390, justify='left').pack(padx=20, pady=8)
        self.v_host.trace_add('write', self._host_changed)
        self.update_idletasks()
        self.geometry('460x' + str(self.winfo_reqheight()))

    def _host_changed(self, *_):
        # Never carry a saved credential over to a different address.
        self.v_token.set('')
        self._remember_changed()

    def _remember_changed(self):
        try:
            host, token = self.v_host.get(), self.v_token.get().strip()
            if token.startswith(PREFIX):
                host, token = read_code(token)
            _preferences.save(host, token, self.v_remember.get())
        except Exception:
            self._connect_status.set('Could not save preferences. You can connect with saving unchecked.')

    def _attempt_connect(self):
        value = self.v_token.get().strip()
        if value.startswith(PREFIX):
            try:
                host, token = read_code(value)
            except ValueError as error:
                self._connect_status.set(str(error))
                return
            self.v_host.set(host)  # Changing the address clears the old token.
            self.v_token.set(token)
        self._remember_changed()
        super()._attempt_connect()

    def _connect_succeeded(self):
        for child in list(self.winfo_children()):
            child.destroy()
        self.geometry('720x620')
        self.minsize(600, 500)
        build(self, self)

    def _on_line(self, text):
        pass  # No console or editing interface in the launcher.

    def _on_disconnected(self, message):
        self._connected = False  # The connection monitor stops our helper.


def open_prefabulator(launcher):
    existing = getattr(launcher, '_prefabulator_window', None)
    if existing is not None and existing.winfo_exists():
        existing.deiconify()
        existing.lift()
        return
    launcher._prefabulator_window = PrefabulatorWindow(launcher)


_original_build_ui = ClientLauncher._build_ui
def _build_ui(self):
    _original_build_ui(self)
    def find_keeper(widget):
        if isinstance(widget, tk.Button) and 'TavernKeeper' in str(widget.cget('text')):
            return widget
        for child in widget.winfo_children():
            found = find_keeper(child)
            if found is not None:
                return found
        return None
    button = find_keeper(self)
    if button is not None:
        _btn(button.master, 'Prefabulator', lambda: open_prefabulator(self),
             font=('Segoe UI', 9), pady=6).pack(side='left', fill='x', expand=True, padx=3)
    else:
        _btn(self, 'Prefabulator', lambda: open_prefabulator(self), 'primary').pack(fill='x', padx=12, pady=6)

ClientLauncher._build_ui = _build_ui


def _same_server(a, b):
    if a.lower() == b.lower():
        return True
    try:
        return socket.gethostbyname(a) == socket.gethostbyname(b)
    except OSError:
        return False


def _auto_connect(launcher):
    """Runs in Tavern's launch worker before the game reads its editor URL."""
    global _active
    host = launcher.v_ip.get().strip() or '127.0.0.1'
    game = Path(launcher.v_exe.get().strip()).parent
    saved = _preferences.read()
    with _session_lock:
        if _active and not _active.closed:
            if not _same_server(getattr(_active, 'host', ''), host):
                _active.close_reason = 'Editor disconnected: the game launcher and Prefabulator use different server addresses. Use the same address in both, then click Retry editor connection.'
                launcher._print(_active.close_reason)
                _active.close()
            elif _active.transport.client._connected and _active.process and _active.process.poll() is None:
                _active.ensure_connection(game)
                return
            else:
                _active.close()
        if not saved.get('protectedToken') or not _same_server(saved.get('host', ''), host):
            return
        if not (game / 'Mods' / 'PrefabEditor.dll').is_file():
            launcher._print('[Prefabulator] Open Prefabulator and install the included player mod first.')
            return
        client = WsConsoleClient()
        bridge = None
        try:
            token = _preferences.token(saved)
            ok, _ = client.connect(saved['host'], token, on_line=lambda _: None,
                                   on_disc=lambda _: bridge.close() if bridge else None)
            if not ok:
                raise RuntimeError('Console connection failed')
            bridge = Bridge(client, game, Path(_tavern_data_dir()) / 'prefab_editor',
                            host=saved['host'], token=token)
            bridge.host = saved['host']
            original_close = bridge.close
            def close_session():
                original_close()
                client.disconnect()
            bridge.close = close_session
            bridge.start()
            _active = bridge
            atexit.register(client.disconnect)
            launcher._print('[Prefabulator] Connected. Press F1 in game. Keep Tavern Launcher open.')
        except Exception:
            if bridge:
                bridge.close()
            client.disconnect()
            launcher._print('[Prefabulator] Auto-connect failed. Open Prefabulator to check your address/token and connect, then relaunch the game.')


_original_do_launch = ClientLauncher._do_launch

def _do_launch(self, *args, **kwargs):
    try:
        _auto_connect(self)
    except Exception:
        self._print('[Prefabulator] Open Prefabulator to reconnect the editor.')
    return _original_do_launch(self, *args, **kwargs)

ClientLauncher._do_launch = _do_launch
