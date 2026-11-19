"""Exercise the new launcher screens with real Tk widgets and a fake console."""
import argparse
from pathlib import Path
import sys
import tempfile
import time
import tkinter as tk
from types import SimpleNamespace
from unittest.mock import patch

parser = argparse.ArgumentParser()
parser.add_argument('tavern', type=Path)
parser.add_argument('deps', type=Path)
args = parser.parse_args()
sys.path[:0] = [str(args.deps.resolve()), str(args.tavern.resolve())]
import addons
addons.__path__.insert(0, str(Path(__file__).resolve().parents[1] / 'tavern-addon'))
from addons.prefab_editor import client as addon
from addons.prefab_editor.settings import Settings


def descendants(widget):
    for child in widget.winfo_children():
        yield child
        yield from descendants(child)


def button(window, caption):
    return next(child for child in descendants(window)
                if isinstance(child, tk.Button) and child.cget('text') == caption)


def label_texts(window):
    return [child.getvar(child.cget('textvariable')) if child.cget('textvariable') else child.cget('text')
            for child in descendants(window) if isinstance(child, tk.Label) and child.winfo_ismapped()]


def until(root, check):
    deadline = time.monotonic() + 3
    while time.monotonic() < deadline:
        root.update()
        if check():
            return
        time.sleep(.02)
    raise AssertionError('Timed out waiting for a launcher UI update')


def visible_inside(window, widget):
    return (widget.winfo_ismapped()
            and widget.winfo_rootx() + widget.winfo_width() <= window.winfo_rootx() + window.winfo_width()
            and widget.winfo_rooty() + widget.winfo_height() <= window.winfo_rooty() + window.winfo_height())


class Bridge:
    def __init__(self, client, game, *args, **kwargs):
        self.transport = SimpleNamespace(client=client)
        self.game = Path(game)
        self.process = None
        self.closed = False
        self.close_reason = ''
        self.url = 'http://127.0.0.1:54000'

    def start(self):
        return self.url

    def ensure_connection(self, *args):
        pass

    def close(self):
        self.closed = True


with tempfile.TemporaryDirectory(prefix='prefab-launcher-layout-') as temp:
    game = Path(temp, 'A Township Tale')
    (game / 'Mods').mkdir(parents=True)
    (game / 'Mods' / 'PrefabEditor.dll').touch()
    root = tk.Tk()
    root.withdraw()
    root.v_ip = tk.StringVar(root, value='192.0.2.11')
    root.v_exe = tk.StringVar(root, value=str(game / 'A Township Tale.exe'))
    callbacks = []
    root.report_callback_exception = lambda kind, error, trace: callbacks.append(error)
    diagnostics = []

    def report(selected, pid):
        diagnostics.append((Path(selected), pid))
        return 'Sample local diagnostics'

    try:
        with patch.object(addon, '_preferences', Settings(temp)), \
             patch.object(addon, 'Bridge', Bridge), \
             patch.object(addon, 'SessionTransport', lambda client: SimpleNamespace(close=lambda: None)), \
             patch.object(addon, 'access_request', lambda *args: {'owner': True}), \
             patch.object(addon, 'connection_diagnostics', report):
            window = addon.PrefabulatorWindow(root)
            root.update()
            assert window.title() == 'Prefabulator'
            assert visible_inside(window, button(window, 'Connect'))
            assert visible_inside(window, button(window, 'Connection diagnostics'))
            masked = next(widget for widget in descendants(window)
                          if isinstance(widget, tk.Entry) and widget.cget('textvariable') == str(window.v_token))
            assert masked.cget('show'), 'credentials must stay masked'
            window.geometry('530x390')
            root.update()
            assert visible_inside(window, button(window, 'Connect'))
            canvas = next(widget for widget in descendants(window) if isinstance(widget, tk.Canvas))
            assert canvas.yview()[1] < 1, 'short window should scroll the form, not hide the footer'
            canvas.yview_moveto(1)
            root.update()
            assert canvas.yview()[1] == 1

            window._ws_client = SimpleNamespace(_connected=True, disconnect=lambda: None)
            window._connected = True
            window.v_token.set('example-owner-credential')
            window._connect_succeeded()
            until(root, lambda: 'Editor connected' in label_texts(window))
            for caption in ('Retry connection', 'Disconnect', 'Change server', 'Diagnostics'):
                assert visible_inside(window, button(window, caption)), caption
            until(root, lambda: button(window, 'Manage moderator access').winfo_ismapped())
            button(window, 'Diagnostics').invoke()
            dialog = window._diagnostics_window
            until(root, lambda: button(dialog, 'Copy report').cget('state') == 'normal')
            assert diagnostics == [(game, None)], diagnostics
            dialog.destroy()

            button(window, 'Disconnect').invoke()
            until(root, lambda: 'Editor disconnected' in label_texts(window))
            assert addon._active.closed
            button(window, 'Retry connection').invoke()
            until(root, lambda: 'Editor connected' in label_texts(window))
            window._on_close()
            root.update()
        assert not callbacks, callbacks
    finally:
        root.destroy()
print('PASS: launcher cards and pinned actions, narrow-window scroll, masked access, editor dashboard, diagnostics and reconnect.')
