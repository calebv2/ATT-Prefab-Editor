"""Exercise launcher-linked server selection with real, hidden Tk forms.

Usage: python check-launcher-server-selection.py TAVERN_SOURCE DEPS [ADDON_SOURCE]
ADDON_SOURCE is the prefab_editor directory; defaults to this repo's add-on.
"""
import argparse
from pathlib import Path
import sys
import tempfile
import tkinter as tk
from types import SimpleNamespace
from unittest.mock import patch

parser = argparse.ArgumentParser()
parser.add_argument('tavern', type=Path)
parser.add_argument('deps', type=Path)
parser.add_argument('source', type=Path, nargs='?', default=Path(__file__).resolve().parents[1] / 'tavern-addon/prefab_editor')
args = parser.parse_args()
sys.path[:0] = [str(args.deps.resolve()), str(args.tavern.resolve())]
import addons
addons.__path__.insert(0, str(args.source.resolve().parent))
import addons.prefab_editor.client as addon
from addons.prefab_editor.invite import make_code
from addons.prefab_editor.settings import Settings
import client.core.tavernkeeper.window as keeper


def descendants(widget):
    for child in widget.winfo_children():
        yield child
        yield from descendants(child)


with tempfile.TemporaryDirectory(prefix='prefab-server-selection-') as directory, \
     patch.object(keeper, '_finish_dark_window', lambda window: None):
    root = tk.Tk()
    root.withdraw()
    root.v_ip = tk.StringVar(root, value='192.0.2.10')
    saves = []
    root._save = lambda: saves.append(root.v_ip.get())
    settings = Settings(directory)
    addon._preferences = settings
    windows = []

    def open_form():
        before = tuple(root.v_ip.trace_info())
        window = addon.PrefabulatorWindow(root)
        windows.append(window)
        window.withdraw()
        assert window._launcher is root
        assert len(root.v_ip.trace_info()) > len(before), 'linked form must observe launcher address changes'
        return window, before

    def close_form(window, previous_traces):
        window._on_close()
        windows.remove(window)
        assert tuple(root.v_ip.trace_info()) == previous_traces, 'closing the form must remove its launcher trace'

    try:
        # The selected join destination takes priority over an unrelated saved session.
        settings.save('127.0.0.1', 'local-owner-test-secret', True)
        window, previous = open_form()
        assert window.v_host.get() == '192.0.2.10'
        assert not window.v_token.get(), 'never send a saved localhost token to a remote server'
        assert '192.0.2.10' in window._server_status.get(), 'show which launcher destination is being used'
        host_entry = next(widget for widget in descendants(window)
                          if isinstance(widget, tk.Entry) and str(widget.cget('textvariable')) == str(window.v_host))
        assert str(host_entry.cget('state')) == 'readonly', 'linked address should be changed in the launcher, not entered twice'
        window.v_token.set('manual-test-secret')
        root.v_ip.set('192.0.2.20')
        assert window.v_host.get() == '192.0.2.20'
        assert not window.v_token.get(), 'switching servers must clear an unsaved credential too'
        assert '192.0.2.20' in window._server_status.get()

        # An authenticated session remains attached to its original console server.
        window._connected = True
        root.v_ip.set('192.0.2.30')
        assert window.v_host.get() == '192.0.2.20', 'launcher edits cannot retarget an authenticated session'
        warning = window._server_status.get()
        assert '192.0.2.20' in warning and '192.0.2.30' in warning, warning
        close_form(window, previous)

        # A blank launcher field has the launcher's local-server meaning.
        root.v_ip.set('')
        settings.save('127.0.0.1', 'local-owner-test-secret', True)
        window, previous = open_form()
        assert window.v_host.get() == '127.0.0.1'
        assert window.v_token.get() == 'local-owner-test-secret'
        close_form(window, previous)

        # Equivalent localhost spelling can retain the same-server saved token.
        root.v_ip.set('localhost')
        settings.save('127.0.0.1', 'local-owner-test-secret', True)
        window, previous = open_form()
        assert window.v_host.get() == 'localhost'
        assert window.v_token.get() == 'local-owner-test-secret'
        close_form(window, previous)

        # A moderator code chooses its embedded server before any console connection.
        root.v_ip.set('127.0.0.1')
        settings.save('127.0.0.1')
        window, previous = open_form()
        calls = []
        def native_connect(form):
            calls.append((root.v_ip.get(), form.v_host.get(), form.v_token.get()))
        with patch.object(addon.TavernKeeperWindow, '_attempt_connect', native_connect):
            window.v_token.set(make_code('moderator.example.test', 'pfa1.personal-test-secret'))
            window._attempt_connect()
            assert calls == [('moderator.example.test', 'moderator.example.test', 'pfa1.personal-test-secret')], calls
            before = (root.v_ip.get(), window.v_host.get())
            window.v_token.set('pfac1.invalid')
            window._attempt_connect()
            assert len(calls) == 1, 'invalid moderator code must never connect'
            assert (root.v_ip.get(), window.v_host.get()) == before, 'invalid code must not change either destination'
            assert 'Invalid moderator code' in window._connect_status.get()
        close_form(window, previous)

        # Exercise the real asynchronous reconnect callback and Tk event loop.
        root.v_ip.set('192.0.2.40')
        settings.save('192.0.2.40', 'old-server-owner-test-secret', True)
        window, previous = open_form()
        window._connected = True
        events = []
        callback_errors = []
        reopened = []
        old_active = addon._active

        class Client:
            _connected = True
            def disconnect(self):
                events.append('client disconnected')
                self._connected = False

        client = Client()
        class Bridge:
            closed = False
            transport = SimpleNamespace(client=client)
            def close(self):
                events.append('bridge closed')
                self.closed = True

        bridge = Bridge()
        window._ws_client = client
        root.v_ip.set('192.0.2.50')
        assert window.v_host.get() == '192.0.2.40'
        assert '192.0.2.50' in window._server_status.get()

        def open_replacement(launcher):
            try:
                events.append('open replacement')
                assert launcher is root
                assert bridge.closed, 'old bridge must finish closing before the fresh form opens'
                assert not client._connected, 'old console client must disconnect on _on_close'
                assert window._launcher_trace is None, 'destroyed form must remove its launcher trace'
                assert tuple(root.v_ip.trace_info()) == previous, 'old trace must be gone before registering the new form'
                replacement, replacement_previous = open_form()
                assert replacement.v_host.get() == '192.0.2.50'
                assert not replacement.v_token.get(), 'old server credential must not appear in replacement form'
                reopened.append((replacement, replacement_previous))
            except BaseException as error:
                callback_errors.append(error)
            finally:
                root.quit()

        def callback_exception(exc_type, error, traceback):
            callback_errors.append(error)
            root.quit()

        prior_exception_handler = root.report_callback_exception
        root.report_callback_exception = callback_exception
        timeout = root.after(5000, root.quit)
        try:
            with patch.object(addon, '_active', bridge), \
                 patch.object(addon, 'open_prefabulator', open_replacement):
                root.after(0, window._reopen_connection)
                root.mainloop()
            assert addon._active is old_active, 'test must restore the active bridge global'
            assert not callback_errors, callback_errors
            assert reopened, 'reconnect did not open a replacement form within five seconds'
            assert events == ['bridge closed', 'client disconnected', 'open replacement'], events
            windows.remove(window)
            close_form(*reopened[0])
        finally:
            root.after_cancel(timeout)
            root.report_callback_exception = prior_exception_handler
    finally:
        for window in windows:
            if window.winfo_exists():
                window._on_close()
        root.destroy()

print('PASS: launcher-prefilled read-only address, credential isolation, live selection changes, connected-session warning, local defaults/aliases, moderator-code routing, trace cleanup and asynchronous reconnect ordering.')
