"""Load a real packaged add-on with uuid unavailable, as in Tavern's EXE."""
import argparse
import builtins
import json
from pathlib import Path
import sys
import tempfile
import tkinter as tk
from unittest.mock import patch
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument('tavern', type=Path)
parser.add_argument('deps', type=Path)
parser.add_argument('archive', type=Path)
args = parser.parse_args()
sys.path[:0] = [str(args.deps), str(args.tavern)]
import addons
from client.core import addon_loader
from client.core.launcher_window import ClientLauncher
import client.core.launcher_window as launcher

native_import = builtins.__import__
blocked = []
def frozen_import(name, *a, **kw):
    if name == 'uuid' or name.startswith('uuid.'):
        blocked.append(name)
        raise ModuleNotFoundError("No module named 'uuid'")
    return native_import(name, *a, **kw)

with tempfile.TemporaryDirectory(prefix='prefab-button-') as folder:
    root = Path(folder)
    with zipfile.ZipFile(args.archive) as archive:
        for name in archive.namelist():
            if name.startswith('addons/prefab_editor/') and (name.endswith('.py') or name.endswith('author.json')):
                archive.extract(name, root)
    addons.__path__.insert(0, str(root / 'addons'))
    enabled = root / 'enabled.json'
    enabled.write_text(json.dumps(['prefab_editor']))
    errors = []
    with patch('builtins.__import__', frozen_import), \
         patch.object(addon_loader, 'addons_dir', lambda: str(root / 'addons')), \
         patch.object(addon_loader, '_enabled_addons_file', lambda: str(enabled)):
        addon_loader.load_enabled_addons('client', errors.append)
        assert not errors, errors
        with patch.object(ClientLauncher, '_header', lambda self: None), \
             patch.object(ClientLauncher, '_poll_ticket_replies_loop', lambda self: None), \
             patch.object(ClientLauncher, '_load', lambda self: None), \
             patch.object(launcher, '_start_hidden', lambda self: self.withdraw()), \
             patch.object(launcher, '_finish_dark_window', lambda self: None), \
             patch.object(launcher, 'attach_help_tab', lambda self: None):
            app = ClientLauncher()
            app.withdraw()
            def walk(widget):
                yield widget
                for child in widget.winfo_children():
                    yield from walk(child)
            buttons = [b for b in walk(app) if isinstance(b, tk.Button) and b.cget('text') == 'Prefabulator']
            assert len(buttons) == 1, len(buttons)
            buttons[0].invoke()
            app.update_idletasks()
            assert app._prefabulator_window.title() == 'Prefabulator'
            app._prefabulator_window.destroy()
            app.destroy()
    assert not blocked, blocked
print('PASS: packaged enabled add-on loads, creates exactly one Prefabulator button and opens its form with uuid unavailable.')
