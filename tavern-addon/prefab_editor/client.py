"""Prefabulator connects the in-game editor using Tavern's console transport."""
import threading
import atexit
import socket
from pathlib import Path
import tkinter as tk
from tkinter import filedialog
from client.core.config import load_cfg
from client.core.tavernkeeper.window import TavernKeeperWindow
from client.core.launcher_window import ClientLauncher
from tavern_shared.paths import _tavern_data_dir
from tavern_shared.theme import AMBER, BG, BORDER, GREEN, MUTED, PARCH, SURF, SURF2, _btn, _mk_scrollbar
from tavern_shared.window_chrome import _finish_dark_window, _start_hidden
from .runtime import Bridge, connection_diagnostics, install, shared_install, SessionTransport
from .invite import PREFIX, read_code
from .settings import Settings
from .access import AccessWindow, request as access_request
from tavern_shared.ws_console_client import WsConsoleClient

_active = None
_session_lock = threading.RLock()
_preferences = Settings(Path(_tavern_data_dir()) / "prefab_editor")
_SUBTLE = '#c0ad90'


def _label(parent, text='', fg=_SUBTLE, font=('Segoe UI', 10), **options):
    label = tk.Label(parent, text=text, bg=parent.cget('bg'), fg=fg, font=font,
                     anchor='w', justify='left', wraplength=540, **options)
    parent.bind('<Configure>', lambda event: label.configure(wraplength=max(100, event.width - 8))
                if event.widget is parent else None, add='+')
    return label


def _action(parent, label, command, style='normal'):
    return _btn(parent, label, command, style, padx=13, pady=8,
                font=('Segoe UI', 10, 'bold' if style == 'primary' else 'normal'),
                disabledforeground=MUTED, highlightbackground=parent.cget('bg'),
                highlightcolor=AMBER, highlightthickness=1)


def _card(parent, title):
    outline = tk.Frame(parent, bg=SURF, highlightbackground=BORDER, highlightthickness=1)
    outline.pack(fill='x', pady=(0, 12))
    card = tk.Frame(outline, bg=SURF)
    card.pack(fill='x', padx=16, pady=14)
    _label(card, title, PARCH, ('Segoe UI', 11, 'bold')).pack(fill='x', pady=(0, 6))
    return card


def _entry(parent, variable, readonly=False, secret=False):
    return tk.Entry(parent, textvariable=variable, bg=BG, fg=PARCH, insertbackground=PARCH,
                    readonlybackground=BG, selectbackground=SURF2, selectforeground=PARCH,
                    highlightbackground=BORDER, highlightcolor=AMBER, highlightthickness=1,
                    relief='flat', bd=8, font=('Consolas', 10),
                    state='readonly' if readonly else 'normal', show='●' if secret else '')


def _check(parent, text, variable, command):
    return tk.Checkbutton(parent, text=text, variable=variable, command=command, bg=SURF, fg=PARCH,
                          activebackground=SURF, activeforeground=PARCH, selectcolor=BG,
                          highlightbackground=SURF, highlightcolor=AMBER, highlightthickness=1,
                          anchor='w', justify='left', font=('Segoe UI', 10))


def _header(parent, title, subtitle):
    header = tk.Frame(parent, bg=BG)
    header.pack(fill='x', padx=24, pady=(18, 14))
    _label(header, title, PARCH, ('Georgia', 24, 'bold')).pack(fill='x')
    _label(header, subtitle).pack(fill='x', pady=(5, 0))


def _layout(window, subtitle):
    _header(window, 'Prefabulator', subtitle)
    border = tk.Frame(window, bg=BG)
    border.pack(side='bottom', fill='x')
    tk.Frame(border, bg=BORDER, height=1).pack(fill='x')
    footer = tk.Frame(border, bg=BG)
    footer.pack(fill='x', padx=24, pady=12)
    viewport = tk.Frame(window, bg=BG)
    viewport.pack(fill='both', expand=True, padx=(24, 10))
    canvas = tk.Canvas(viewport, bg=BG, highlightthickness=0, bd=0, takefocus=False)
    scrollbar = _mk_scrollbar(viewport, canvas.yview)
    scrollbar.pack(side='right', fill='y', padx=(4, 0))
    canvas.pack(fill='both', expand=True)
    canvas.configure(yscrollcommand=scrollbar.set)
    body = tk.Frame(canvas, bg=BG)
    slot = canvas.create_window(0, 0, anchor='nw', window=body)
    body.bind('<Configure>', lambda event: canvas.configure(scrollregion=canvas.bbox(slot)))
    canvas.bind('<Configure>', lambda event: canvas.itemconfigure(slot, width=event.width))

    def wheel(event):
        if body.winfo_ismapped() and str(event.widget).startswith(str(viewport) + '.'):
            canvas.yview_scroll(int(-event.delta / 120), 'units')
            return 'break'

    def reveal(event):
        if not str(event.widget).startswith(str(body) + '.'):
            return
        widget = event.widget
        top = widget.winfo_rooty() - body.winfo_rooty()
        height = canvas.winfo_height()
        offset = canvas.canvasy(0)
        if top < offset:
            canvas.yview_moveto(top / max(1, body.winfo_height()))
        elif top + widget.winfo_height() > offset + height:
            canvas.yview_moveto((top + widget.winfo_height() - height) / max(1, body.winfo_height()))

    window.bind('<MouseWheel>', wheel)
    window.bind('<FocusIn>', reveal)
    return body, footer


def _window_size(window, width, height):
    screen_width, screen_height = window.winfo_screenwidth(), window.winfo_screenheight()
    width = min(width, screen_width - 48)
    height = min(height, screen_height - 96)
    window.minsize(min(width, 530), min(height, 390))
    window.geometry(f'{width}x{height}+{(screen_width - width) // 2}+{(screen_height - height) // 2}')


def _background(schedule, action, done):
    completed = []

    def work():
        try:
            completed.append((action(), None))
        except Exception as error:
            completed.append((None, str(error)))

    def poll():
        if completed:
            done(*completed[0])
        else:
            schedule(60, poll)

    threading.Thread(target=work, daemon=True).start()
    schedule(60, poll)


def build(parent, window):
    """Dashboard shown after the server console has accepted the credential."""
    body, footer = _layout(parent, 'Install the player mod and keep the editor connected while you play.')
    game = tk.StringVar(window, value=window._game_folder())
    status = tk.StringVar(window)
    summary = tk.StringVar(window)
    details = tk.StringVar(window)
    access_status = tk.StringVar(window)
    shared = shared_install()
    busy = False
    tracked_bridge = None
    jobs = set()

    def schedule(delay, callback):
        def run():
            jobs.discard(job)
            callback()
        job = body.after(delay, run)
        jobs.add(job)

    def cancel_jobs(event):
        if event.widget is body:
            for job in jobs:
                body.after_cancel(job)
            jobs.clear()
    body.bind('<Destroy>', cancel_jobs, add='+')

    connection = _card(body, 'Editor connection')
    state_label = _label(connection, textvariable=summary, fg=AMBER, font=('Segoe UI', 11, 'bold'))
    state_label.pack(fill='x')
    _label(connection, textvariable=window._server_status, font=('Segoe UI', 9)).pack(fill='x', pady=(3, 0))
    _label(connection, textvariable=status, fg=PARCH).pack(fill='x', pady=(6, 0))
    detail_label = _label(connection, textvariable=details, font=('Segoe UI', 9))

    installation = _card(body, 'Shared Mods folder' if shared else 'Player mod')
    folder_row = tk.Frame(installation, bg=SURF)
    folder_row.pack(fill='x', pady=(0, 7))
    folder_entry = _entry(folder_row, game)
    folder_entry.pack(side='left', fill='x', expand=True)

    access = _card(body, 'Access')
    access_actions = tk.Frame(access, bg=SURF)
    access_actions.pack(fill='x')
    access_note = _label(access, textvariable=access_status, font=('Segoe UI', 9))

    def set_state(label, message, color=AMBER, detail=''):
        summary.set(label)
        state_label.configure(fg=color)
        status.set(message)
        details.set(detail)
        if detail:
            detail_label.pack(fill='x', pady=(6, 0))
        else:
            detail_label.pack_forget()

    def sync_buttons():
        retry_button.configure(state='disabled' if busy else 'normal')
        disconnect_button.configure(state='normal' if not busy and tracked_bridge and not tracked_bridge.closed else 'disabled')
        if change_button is not None:
            change_button.configure(state='disabled' if busy else 'normal')
        install_button.configure(state='disabled' if busy else 'normal')
        browse_button.configure(state='disabled' if busy else 'normal')
        folder_entry.configure(state='readonly' if busy else 'normal')

    def background(action, done, message):
        nonlocal busy
        if busy:
            return
        busy = True
        set_state('Working…', message)
        sync_buttons()

        def finish(result, error):
            nonlocal busy
            busy = False
            if error is None:
                done(result)
            else:
                set_state('Action failed', error, '#f2a3a0')
            sync_buttons()

        _background(schedule, action, finish)

    def choose():
        path = filedialog.askdirectory(parent=window, title='Choose the folder containing A Township Tale_Data')
        if path:
            game.set(path)

    def install_player():
        if not game.get().strip():
            choose()
        path = game.get().strip()
        if not path:
            return

        def installed(_):
            if shared:
                set_state('Mods installed', 'Start the server, reconnect Prefabulator, then launch the player game.', GREEN)
            else:
                start_editor()

        background(lambda: install(path, 'client'), installed, 'Installing the included mod files…')

    def connection_ready(bridge, path):
        target = Path(path).resolve() / 'UserData' / 'PrefabEditorConnection.json'
        set_state('Editor connected', 'Join the connected server and press F1. If the game is already open, relaunch it. Keep Prefabulator open.',
                  GREEN, f'Local editor   {bridge.url}\nConnection file   {target}')

    def start_editor():
        path = game.get().strip()
        if not path or not (Path(path) / 'Mods' / 'PrefabEditor.dll').is_file():
            set_state('Player mod needed', 'Choose your game folder, then install the included player mod.')
            return
        if not window._connected:
            set_state('Console disconnected', 'Use Change server to reconnect to the console.')
            return
        connection_host = window.v_host.get().strip() or '127.0.0.1'
        connection_token = window.v_token.get().strip()
        game_host = window._game_host or connection_host

        def connect_editor():
            global _active
            with _session_lock:
                if _active and not _active.closed:
                    if not _same_server(getattr(_active, 'host', ''), connection_host) or not _same_server(getattr(_active, 'game_host', _active.host), game_host):
                        raise RuntimeError('Disconnect the existing editor before changing servers.')
                    _active.ensure_connection(path)
                    return _active
                bridge = Bridge(window._ws_client, path, Path(_tavern_data_dir()) / 'prefab_editor',
                                host=connection_host, token=connection_token,
                                access_transport=management_transport)
                bridge.host = connection_host
                bridge.game_host = game_host
                _active = bridge
                try:
                    bridge.start()
                except Exception as error:
                    bridge.close_reason = 'Editor connection failed: ' + str(error)
                    bridge.close()
                    raise
                return bridge

        def started(bridge):
            nonlocal tracked_bridge
            tracked_bridge = bridge
            connection_ready(bridge, path)

        background(connect_editor, started, 'Connecting the in-game editor…')

    def retry():
        if not window._connected and window._launcher is not None:
            window._reopen_connection()
        else:
            start_editor()

    def stop_editor():
        if tracked_bridge and not tracked_bridge.closed:
            background(tracked_bridge.close, lambda _: set_state(
                'Editor disconnected', 'Click Retry connection before relaunching the game.'),
                'Disconnecting the editor…')

    def forget_token():
        try:
            saved = _preferences.read()
            _preferences.save(saved.get('host', ''), game_host=saved.get('gameHost'))
            access_status.set('Saved access removed. Enter your token or code next time.')
        except OSError:
            access_status.set('Could not remove saved access. Check your user settings folder permissions.')
        access_note.pack(fill='x', pady=(6, 0))

    # The owner check and access management use the authenticated WebSocket session.
    management_transport = (None if window.v_token.get().strip().startswith('pfa1.')
                            else SessionTransport(window._ws_client))
    access_window = None

    def show_access():
        nonlocal access_window
        if access_window is not None and access_window.winfo_exists():
            access_window.lift()
        else:
            access_window = AccessWindow(window, management_transport,
                                         window._game_host or window.v_host.get().strip() or '127.0.0.1')

    def access_ready(result, error):
        if error is None and result.get('owner') is True:
            owner_button.pack(side='left', padx=(0, 8), before=forget_button)

    browse_button = _action(folder_row, 'Browse…', choose)
    browse_button.pack(side='left', padx=(8, 0))
    install_button = _action(installation, 'Install or update both mods' if shared else 'Install or update player mod',
                             install_player)
    install_button.pack(anchor='w')
    _label(installation, 'Close the server and player game before installing.' if shared
           else 'Close the player game before installing.', font=('Segoe UI', 9)).pack(fill='x', pady=(6, 0))
    owner_button = _action(access_actions, 'Manage moderator access', show_access)
    forget_button = _action(access_actions, 'Forget saved access', forget_token)
    forget_button.pack(side='left')

    _action(footer, 'Diagnostics', lambda: window._show_connection_diagnostics(game.get().strip())).pack(side='left')
    retry_button = _action(footer, 'Retry connection', retry, 'primary')
    retry_button.pack(side='right')
    disconnect_button = _action(footer, 'Disconnect', stop_editor)
    disconnect_button.pack(side='right', padx=(0, 8))
    change_button = None
    if window._launcher is not None:
        change_button = _action(footer, 'Change server', window._reopen_connection)
        change_button.pack(side='right', padx=(0, 8))

    if management_transport is not None:
        schedule(500, lambda: _background(schedule, lambda: access_request(management_transport, 'self'), access_ready))

    def destroyed(event):
        if event.widget is window:
            if management_transport is not None:
                management_transport.close()
            if _active and _active.transport.client is window._ws_client:
                threading.Thread(target=_active.close, daemon=True).start()
    window.bind('<Destroy>', destroyed, add='+')

    def monitor():
        nonlocal tracked_bridge
        bridge = tracked_bridge
        if bridge is not None and not busy:
            if bridge.closed:
                if bridge.close_reason:
                    set_state('Editor disconnected', bridge.close_reason)
                tracked_bridge = None
                sync_buttons()
            elif not bridge.transport.client._connected or (bridge.process and bridge.process.poll() is not None):
                bridge.close_reason = 'Reconnect Prefabulator, then relaunch the player game.'
                background(bridge.close, lambda _: set_state('Editor disconnected', bridge.close_reason),
                           'Closing the stopped editor connection…')
            else:
                try:
                    bridge.ensure_connection()
                except (OSError, RuntimeError) as error:
                    set_state('Connection file unavailable', str(error), '#f2a3a0')
                else:
                    if summary.get() == 'Connection file unavailable':
                        connection_ready(bridge, bridge.game)
        schedule(1500, monitor)

    sync_buttons()
    set_state('Starting editor', 'The server console accepted your access.')
    schedule(0, start_editor)
    schedule(1500, monitor)


class PrefabulatorWindow(TavernKeeperWindow):
    """Use Tavern's console login with Prefabulator's own launcher screens."""
    def __init__(self, parent):
        self._launcher = parent if hasattr(parent, 'v_ip') else None
        self._launcher_trace = None
        self._game_host = None
        self._connecting_route = None
        self._server_status = tk.StringVar(master=parent, value='')
        super().__init__(parent)
        if self._launcher is not None:
            self._launcher_trace = self._launcher.v_ip.trace_add('write', self._launcher_changed)
            self.bind('<Destroy>', self._remove_launcher_trace, add='+')

    def _launcher_host(self):
        if self._launcher is not None:
            return self._launcher.v_ip.get().strip() or '127.0.0.1'
        return None

    def _game_folder(self):
        path_field = getattr(self._launcher, 'v_exe', None)
        game_exe = path_field.get().strip() if path_field is not None else ''
        game_exe = game_exe or load_cfg().get('game_exe')
        return str(Path(game_exe).parent) if game_exe else ''

    def _show_connection_diagnostics(self, game_folder=None):
        existing = getattr(self, '_diagnostics_window', None)
        if existing is not None and existing.winfo_exists():
            existing.deiconify()
            existing.lift()
            return
        if game_folder is None:
            game_folder = self._game_folder()
        process = getattr(_active, 'process', None)
        helper_pid = process.pid if process and process.poll() is None else None

        dialog = tk.Toplevel(self)
        _start_hidden(dialog)
        self._diagnostics_window = dialog
        dialog.title('Prefabulator connection diagnostics')
        dialog.configure(bg=BG)
        _window_size(dialog, 680, 490)
        _header(dialog, 'Connection diagnostics', 'Check the game installation, local helper, and editor ports.')
        frame = tk.Frame(dialog, bg=SURF, highlightbackground=BORDER, highlightthickness=1)
        frame.pack(fill='both', expand=True, padx=24)
        scrollbar = _mk_scrollbar(frame, lambda *args: body.yview(*args))
        scrollbar.pack(side='right', fill='y')
        body = tk.Text(frame, wrap='word', bg=SURF, fg=PARCH, padx=12, pady=10, bd=0,
                       highlightthickness=0, font=('Segoe UI', 10), yscrollcommand=scrollbar.set)
        body.pack(side='left', fill='both', expand=True)
        body.insert('1.0', 'Checking the selected game folder, editor helpers and local ports...')
        body.configure(state='disabled')
        actions = tk.Frame(dialog, bg=BG)
        actions.pack(fill='x', padx=24, pady=12)
        report = []
        job = [None]

        def copy_report():
            dialog.clipboard_clear()
            dialog.clipboard_append(report[0])

        copy_button = _action(actions, 'Copy report', copy_report, 'primary')
        copy_button.configure(state='disabled')
        copy_button.pack(side='right')
        _action(actions, 'Close', dialog.destroy).pack(side='left')

        def scan():
            try:
                report.append(connection_diagnostics(game_folder, helper_pid))
            except Exception:
                report.append('Could not complete the local diagnostics. Close this window and try again.')

        def show_report():
            job[0] = None
            if not report:
                job[0] = dialog.after(100, show_report)
                return
            body.configure(state='normal')
            body.delete('1.0', 'end')
            body.insert('1.0', report[0])
            body.configure(state='disabled')
            copy_button.configure(state='normal')

        def stop_polling(event):
            if event.widget is dialog and job[0] is not None:
                dialog.after_cancel(job[0])

        dialog.bind('<Destroy>', stop_polling, add='+')
        dialog.bind('<Escape>', lambda event: dialog.destroy())
        threading.Thread(target=scan, daemon=True).start()
        job[0] = dialog.after(100, show_report)
        _finish_dark_window(dialog)

    def _update_server_status(self):
        host = self.v_host.get().strip() or '127.0.0.1'
        selected = self._launcher_host()
        game_host = self._game_host or host
        if self._connected and selected and not _same_server(game_host, selected):
            self._server_status.set('Connected game server: ' + game_host + '\nLauncher selected: ' + selected +
                                    '\nServer changed. Click Change server before joining.')
        elif self._connected:
            self._server_status.set('Game server: ' + game_host +
                                    ('   ·   Console: ' + host if host != game_host else ''))
        elif selected:
            self._server_status.set('Game server in Tavern Launcher: ' + selected)
        else:
            self._server_status.set('Select your game server, then connect to its console.')

    def _launcher_changed(self, *_):
        host = self._launcher_host()
        if not self._connected and not self._connecting_route and not _same_server(self._game_host or '', host):
            self._game_host = host
            self.v_local_console.set(False)
            self.v_host.set(host)  # A different game server never inherits a local-console pairing or credential.
        self._update_server_status()

    def _local_console_changed(self):
        if self._connecting_route:
            self.v_local_console.set(self._connecting_route[3])
            return
        self._game_host = self._launcher_host() or self._game_host or '127.0.0.1'
        self.v_host.set('127.0.0.1' if self.v_local_console.get() else self._game_host)
        self._update_server_status()

    def _remove_launcher_trace(self, event):
        if event.widget is self and self._launcher_trace is not None:
            self._launcher.v_ip.trace_remove('write', self._launcher_trace)
            self._launcher_trace = None

    def _reopen_connection(self):
        if self._launcher is None or getattr(self, '_reconnecting', False):
            return
        self._reconnecting = True
        def reopen():
            if self.winfo_exists():
                self._on_close()
                open_prefabulator(self._launcher)
        def stop():
            with _session_lock:
                if _active and _active.transport.client is self._ws_client:
                    _active.close()
            self.after(0, reopen)
        threading.Thread(target=stop, daemon=True).start()

    def _build_connect_form(self):
        self.title('Prefabulator')
        _window_size(self, 680, 585)
        body, footer = _layout(self, 'Connect the in-game editor to a Township Tale server.')
        saved = _preferences.read()
        selected = self._launcher_host() or saved.get('gameHost') or saved.get('host', '')
        self._game_host = selected or '127.0.0.1'
        same_saved_game = _same_server(saved.get('gameHost', saved.get('host', '')), self._game_host)
        use_local = same_saved_game and saved.get('gameHost') and saved.get('host') == '127.0.0.1'
        host = '127.0.0.1' if use_local else selected
        same_saved_server = same_saved_game and _same_server(saved.get('host', ''), host)
        self.v_host = tk.StringVar(self, value=host)
        self.v_token = tk.StringVar(self)
        self.v_local_console = tk.BooleanVar(self, value=bool(use_local))
        self.v_remember = tk.BooleanVar(self, value=bool(saved.get('protectedToken')))
        self._connect_status = tk.StringVar(self)
        try:
            self.v_token.set(_preferences.token(saved) if same_saved_server else '')
        except Exception:
            self._connect_status.set('Saved token unavailable. Enter it again.')

        server = _card(body, 'Server')
        if self._launcher is not None:
            _label(server, textvariable=self._server_status, fg=PARCH).pack(fill='x')
        _label(server, 'Console address', fg=PARCH, font=('Segoe UI', 9, 'bold')).pack(fill='x', pady=(7, 3))
        host_entry = _entry(server, self.v_host, readonly=self._launcher is not None)
        host_entry.pack(fill='x')
        if self._launcher is not None:
            _check(server, 'I host this server on this PC', self.v_local_console,
                   self._local_console_changed).pack(fill='x', pady=(7, 0))
            _label(server, 'Uses localhost for the console and keeps the Join Server address from Tavern Launcher.',
                   font=('Segoe UI', 9)).pack(fill='x', padx=3, pady=(1, 0))

        access = _card(body, 'Moderator code or owner console token')
        token_entry = _entry(access, self.v_token, secret=True)
        token_entry.pack(fill='x')
        _label(access, 'A moderator code selects its server automatically.', font=('Segoe UI', 9)).pack(fill='x', pady=(6, 0))
        _check(access, 'Save access and connect automatically on this server', self.v_remember,
               self._remember_changed).pack(fill='x', pady=(6, 0))
        _label(access, 'Saved access is encrypted for this Windows account.', font=('Segoe UI', 9)).pack(fill='x', padx=3)

        _label(footer, textvariable=self._connect_status, fg='#f2a3a0',
               font=('Segoe UI', 9)).pack(fill='x', pady=(0, 6))
        actions = tk.Frame(footer, bg=BG)
        actions.pack(fill='x')
        _action(actions, 'Connection diagnostics', self._show_connection_diagnostics).pack(side='left')
        self._connect_btn = _action(actions, 'Connect', self._attempt_connect, 'primary')
        self._connect_btn.pack(side='right')
        host_entry.bind('<Return>', lambda event: token_entry.focus_set())
        token_entry.bind('<Return>', lambda event: self._attempt_connect()
                         if str(self._connect_btn.cget('state')) == 'normal' else None)
        self.v_host.trace_add('write', self._host_changed)
        self._update_server_status()
        (token_entry if self._launcher is not None else host_entry).focus_set()

    def _host_changed(self, *_):
        # Never carry a saved credential over to a different address.
        self.v_token.set('')
        self._remember_changed()
        self._update_server_status()

    def _remember_changed(self):
        try:
            host, token = self.v_host.get(), self.v_token.get().strip()
            game_host = self._game_host if self.v_local_console.get() else None
            if token.startswith(PREFIX):
                host, token = read_code(token)
                game_host = None  # Invitation credentials belong only to their embedded server.
            _preferences.save(host, token, self.v_remember.get(), game_host=game_host)
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
            self.v_local_console.set(False)  # A code chooses its own console; never silently redirect it to localhost.
            self._game_host = host
            if self._launcher is not None:
                self._launcher.v_ip.set(host)  # A code selects its own server; never send it to another one.
            self.v_host.set(host)  # Changing the address clears the old token.
            self.v_token.set(token)
        self._game_host = self._launcher_host() or self._game_host or self.v_host.get()
        self._remember_changed()
        if not self.v_token.get().strip():
            self._connect_status.set('Enter your moderator code or owner console token.')
            return
        self._connecting_route = (self._game_host, self.v_host.get().strip() or '127.0.0.1', self.v_token.get().strip(), self.v_local_console.get())
        super()._attempt_connect()

    def _connect_failed(self, message):
        self._connecting_route = None
        super()._connect_failed(message)
        self._launcher_changed()

    def _connect_succeeded(self):
        if self._connecting_route:
            self._game_host, host, token, local = self._connecting_route
            self._connecting_route = None
            self.v_local_console.set(local)
            self.v_host.set(host)
            self.v_token.set(token)
            self._remember_changed()
        for child in list(self.winfo_children()):
            child.destroy()
        self._update_server_status()
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
            if not _same_server(getattr(_active, 'game_host', getattr(_active, 'host', '')), host):
                _active.close_reason = ('Editor disconnected: Prefabulator is connected to ' + getattr(_active, 'game_host', getattr(_active, 'host', '')) +
                                        ', but the launcher selected ' + host + '. Open Prefabulator and click Change server / reconnect before joining.')
                launcher._print(_active.close_reason)
                _active.close()
            elif _active.transport.client._connected and _active.process and _active.process.poll() is None:
                _active.ensure_connection(game)
                return
            else:
                _active.close()
        if not saved.get('protectedToken') or not _same_server(saved.get('gameHost', saved.get('host', '')), host):
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
            bridge.game_host = saved.get('gameHost', saved['host'])
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
