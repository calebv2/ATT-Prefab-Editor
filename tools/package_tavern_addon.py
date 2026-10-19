"""Build separate player/server Tavern add-on ZIPs from the staged mod builds."""
from pathlib import Path
import argparse
import hashlib
import json
import subprocess
import urllib.request
import zipfile

repo = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--node', type=Path, default=Path(r'C:\Program Files\nodejs\node.exe'))
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=True)
source = repo / 'tavern-addon/prefab_editor'
version = subprocess.check_output([str(args.node), '--version'], text=True).strip()
license_text = urllib.request.urlopen('https://raw.githubusercontent.com/nodejs/node/' + version + '/LICENSE', timeout=30).read()

for side in ('Player', 'Server'):
    entries = {}
    def add(path, target):
        path = Path(path)
        if path.is_dir():
            for file in path.rglob('*'):
                if file.is_file():
                    add(file, target + '/' + file.relative_to(path).as_posix())
        else:
            name = path.name.lower()
            legal = name.startswith(('license', 'licence', 'copying', 'notice', 'copyright'))
            if '__pycache__' in path.parts or (path.suffix.lower() in ('.md', '.txt', '.rst') and not legal):
                return
            entries['addons/prefab_editor/' + target] = path.read_bytes()
    manifest = json.loads((source / 'author.json').read_text())
    if side == 'Player':
        for name in ('__init__.py', 'author.json', 'runtime.py', 'settings.py', 'client.py'):
            add(source / name, name)
    entries['START HERE.txt'] = (repo / ('tavern-addon/' + side.lower() + '-instructions.txt')).read_bytes()
    entries['LICENSE'] = (repo / 'LICENSE').read_bytes()
    if side == 'Server':
        entries['Mods/PrefabEditorCore.dll'] = (repo / 'server/server-kit/dist/PrefabEditorCore.dll').read_bytes()
    else:
        add(source / 'bridge.js', 'bridge.js')
        add(args.node, 'runtime/node.exe')
        entries['addons/prefab_editor/runtime/LICENSE'] = license_text
        for name in ('PrefabEditor.dll', 'PrefabEditorCatalog.json', 'PrefabEditorImages'):
            add(repo / 'client/player-kit/dist' / name, 'payload/' + name)
        for name in ('server.js', 'lib', 'public'):
            add(repo / 'server/server-kit/panel' / name, 'panel/' + name)
        add(repo / 'server/server-kit/data/prefabs.json', 'data/prefabs.json')
        for name in ('server.js', 'package.json', 'package-lock.json', 'node_modules', 'public', 'tools'):
            add(repo / 'client/player-kit/workbench' / name, 'workbench/' + name)
        for name in ('component-hashes.json', 'dockedTypes.json', 'itemCodes.json', 'liquids.json', 'prefabs.json', 'slot-refs'):
            add(repo / 'client/player-kit/workbench/data' / name, 'workbench/data/' + name)
    label = 'Player-Addon-and-Mod' if side == 'Player' else 'Server-Mod-Only'
    archive = args.output / ('Prefab-Editor-' + label + '-' + manifest['version'] + '.zip')
    with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as bundle:
        for name, data in sorted(entries.items()):
            bundle.writestr(name, data)
    with zipfile.ZipFile(archive) as bundle:
        assert bundle.testzip() is None
        if side == 'Server':
            assert set(bundle.namelist()) == {'Mods/PrefabEditorCore.dll', 'START HERE.txt', 'LICENSE'}
        else:
            assert 'addons/prefab_editor/client.py' in bundle.namelist()
            assert 'addons/prefab_editor/payload/PrefabEditor.dll' in bundle.namelist()
            assert not any(n.endswith('server.py') or n.endswith('PrefabEditorCore.dll') for n in bundle.namelist())
        assert not any('private' in n.lower() or 'console_token' in n.lower() or '.priv.xml' in n for n in bundle.namelist())
    print(archive, archive.stat().st_size, hashlib.sha256(archive.read_bytes()).hexdigest())
