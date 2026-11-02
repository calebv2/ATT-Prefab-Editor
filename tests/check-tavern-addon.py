"""Exercise the packaged runtime and HTTP API over a simulated Tavern session."""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import threading
import urllib.request
import urllib.error
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument('archive', type=Path)
args = parser.parse_args()
os.environ['PREFAB_TEST_WORKBENCH_PORT'] = '0'


class Client:
    def __init__(self):
        self._connected = True
        self._lock = threading.Lock()
        self._cmd_id = 10
        self._ws = self
        self.original_messages = []
        self._handle = lambda raw: self.original_messages.append(raw)
        self.commands = []

    def send(self, wire):
        request = json.loads(wire)
        cmd = request['content']
        self.commands.append(cmd)
        if cmd == 'no-reply':
            return
        if cmd == 'edit capabilities':
            value = {'ok': True, 'tavernBridge': 1}
        elif cmd.startswith('edit access '):
            value = {'ok': True, 'owner': getattr(self, 'owner', False), 'tabs': ['build']}
        elif cmd == 'edit infoboards':
            value = {'ok': True, 'configured': True, 'boards': []}
        elif cmd.startswith('edit infoboardset '):
            value = {'ok': True, 'key': '1.0,2.0,3.0', 'board': {'text': 'remote'}}
        else:
            value = {'ok': True, 'players': []}
        data = {'ResultString': json.dumps(value), 'Result': None}
        if cmd == 'fail':
            data = {'Exception': 'Rejected by server'}
        reply = {'type': 'CommandResult', 'commandId': request['id'], 'data': data}
        self._handle(json.dumps(reply))


with tempfile.TemporaryDirectory(prefix='tavern-addon-test-') as folder:
    root = Path(folder)
    with zipfile.ZipFile(args.archive) as bundle:
        bundle.extractall(root)
    source = root/'addons/prefab_editor/runtime.py'
    spec = importlib.util.spec_from_file_location('prefab_test_runtime', source)
    runtime = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(runtime)
    game = root/'game'
    (game/'UserData').mkdir(parents=True)
    settings = game/'UserData/PrefabEditorConnection.json'
    previous = b'{"panelUrl":"http://127.0.0.1:1766"}'
    settings.write_bytes(previous)
    client = Client()
    original = client._handle
    bridge = runtime.Bridge(client, game, root/'state')
    try:
        url = bridge.start()
        assert json.loads(settings.read_bytes())['panelUrl'] == url
        def request(path, body=None, origin=None):
            headers = {'Content-Type': 'application/json'}
            if origin:
                headers['Origin'] = origin
            req = urllib.request.Request(url+path, data=None if body is None else json.dumps(body).encode(), headers=headers)
            with urllib.request.urlopen(req, timeout=10) as response:
                return json.load(response)
        assert request('/api/access')['data']['tabs'] == ['build']
        assert request('/api/edit/players')['data']['ok']
        assert request('/api/infoboards')['configured']
        assert request('/api/infoboards/update', {'key':'1.0,2.0,3.0','text':'remote','rotation':None})['board']['text'] == 'remote'
        assert request('/api/command', {'command':'fail'})['ok'] is False
        before = len(client.commands)
        try:
            request('/api/command', {'command':'edit players'}, 'https://untrusted.example')
            raise AssertionError('Cross-origin command accepted')
        except urllib.error.HTTPError as error:
            assert error.code == 403
        assert len(client.commands) == before
        # Shared Tavern commands still reach Tavern's original receiver.
        client._handle(json.dumps({'type':'CommandResult','commandId':999999,'data':{}}))
        assert client.original_messages
        try:
            bridge.transport.capture('no-reply', timeout=.02)
            raise AssertionError('Timeout incorrectly returned success')
        except RuntimeError as error:
            assert 'outcome may be unknown' in str(error)
        assert client.commands.count('no-reply') == 1
        client._connected = False
        try:
            bridge.transport.capture('edit players')
            raise AssertionError('Disconnected transport accepted command')
        except RuntimeError:
            pass
    finally:
        bridge.close()
    assert settings.read_bytes() == previous
    assert client._handle is original
    assert bridge.process.poll() is not None
    assert not bridge.transport.pending
    # Regression: native owner HTTP accepts editor commands but not edit access.
    # Exercise the real panel endpoint with both transports present.
    client = Client()
    client.owner = True
    original = client._handle
    http_commands = []
    def owner_http(self, command, timeout=25):
        assert not command.startswith('edit access '), 'owner permission query sent to native HTTP'
        http_commands.append(command)
        value = {'ok': True, 'tavernBridge': 1} if command == 'edit capabilities' else {'ok': True, 'players': []}
        return {'data': {'ResultString': json.dumps(value)}}
    runtime.HttpSessionTransport.capture = owner_http
    bridge = runtime.Bridge(client, game, root/'owner-state', host='example.test', token='owner-test')
    try:
        url = bridge.start()
        access = request('/api/access')
        assert access['ok'] and access['data']['owner'] and access['data']['tabs'] == ['build']
        assert request('/api/edit/players')['data']['ok']
        assert len(client.commands) == 1 and client.commands[0].startswith('edit access ')
        assert http_commands == ['edit capabilities', 'edit players']
    finally:
        bridge.close()
    assert client._handle is original
    assert settings.read_bytes() == previous
    print('PASS: owner panel access endpoint uses WebSocket; ordinary owner commands use HTTP exactly once.')
    print('PASS: packaged runtime startup, shared console, command errors, server Info Boards, origin rejection, timeout without retries, disconnect, helper shutdown and settings restoration.')
