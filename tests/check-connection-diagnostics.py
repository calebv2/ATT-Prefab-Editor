"""Exercise the real diagnostics report with simulated Windows process data."""
import json
import os
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
from unittest.mock import patch


sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tavern-addon'))
from prefab_editor import runtime


with tempfile.TemporaryDirectory(prefix='prefab-diagnostics-') as directory:
    game = Path(directory)
    mods = game / 'Mods'
    mods.mkdir()
    (mods / 'PrefabEditor.dll').touch()
    (mods / 'PrefabEditorCore.dll').touch()
    user_data = game / 'UserData'
    user_data.mkdir()
    descriptor = user_data / 'PrefabEditorConnection.json'
    token = 'private-test-console-token'
    descriptor.write_text(json.dumps({'panelUrl': 'http://127.0.0.1:54321', 'token': token}), encoding='utf-8')

    scan = {
        'processesAvailable': True,
        'portsAvailable': True,
        'helpers': [
            {'ProcessId': 101, 'ParentProcessId': 100, 'CommandLine': 'node.exe bridge.js ' + token},
            {'ProcessId': 202, 'ParentProcessId': 200},
        ],
        'listeners': [
            {'LocalPort': 1767, 'OwningProcess': 101},
            {'LocalPort': 1766, 'OwningProcess': 303},
        ],
    }
    response = SimpleNamespace(stdout=json.dumps(scan), stderr=token)

    with patch.object(runtime, 'os', SimpleNamespace(name='nt', environ=os.environ, getpid=lambda: 100)), \
         patch.object(runtime.subprocess, 'run', return_value=response) as run:
        report = runtime.connection_diagnostics(game, helper_pid=101)
        assert 'Another Prefabulator helper is running (PID 202, launcher PID 200)' in report
        assert 'Another Prefabulator helper is running (PID 101,' not in report
        assert 'Local port 1767 is listening (PID 101, this launcher’s helper)' in report
        assert 'Local port 1766 is listening (PID 303, unidentified process)' in report
        assert 'One player DLL and one server DLL are expected' in report
        assert 'Possible duplicate DLLs' not in report
        assert 'points to local port 54321' in report
        assert 'points to a port with no listener' in report
        assert token not in report and 'node.exe bridge.js' not in report

        command = run.call_args.args[0]
        assert '-Command' in command and '@(1766,1767,54321)' in command[-1]
        assert 'Select-Object ProcessId,ParentProcessId)' in command[-1]

        report = runtime.connection_diagnostics(game, helper_pid=None)
        assert 'Helper started by this launcher (PID 101)' in report
        assert 'Another Prefabulator helper is running (PID 101,' not in report

        for filename in ['PrefabEditor (copy).dll', 'PrefabEditorCore (copy).dll']:
            duplicate = mods / filename
            duplicate.touch()
            try:
                assert 'Possible duplicate DLLs' in runtime.connection_diagnostics(game, helper_pid=101)
            finally:
                duplicate.unlink()

        scan.update(processesAvailable=False, portsAvailable=False, helpers=[], listeners=[])
        response.stdout = json.dumps(scan)
        report = runtime.connection_diagnostics(game, helper_pid=None)
        assert 'Windows process details were unavailable' in report
        assert 'Windows listener details were unavailable' in report
        assert 'No other matching Prefabulator helper found' not in report
        assert 'points to a port with no listener' not in report

print('PASS: diagnostics distinguish active and additional helpers, shared DLLs, stale ports, and unavailable scans.')
