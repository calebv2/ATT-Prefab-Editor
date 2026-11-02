"""Moderator invitations and transport selection without a live server."""
import importlib.util
import json
from pathlib import Path
import threading
import tempfile

source = Path(__file__).resolve().parents[1] / 'tavern-addon/prefab_editor'
def load(name):
    spec = importlib.util.spec_from_file_location(name, source / (name + '.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module

invite, runtime = load('invite'), load('runtime')
token = 'pfa1.test-secret'
assert invite.read_code(invite.make_code('server.example', token)) == ('server.example', token)
for host, secret in [('server.example', 'owner-secret'), ('https://server.example', token), ('', token)]:
    try:
        invite.make_code(host, secret)
    except ValueError:
        pass
    else:
        raise AssertionError('invalid invitation accepted')
for code in ['pfac1.', 'pfac1.invalid', 'pfac1.' + 'a' * 5000]:
    try:
        invite.read_code(code)
    except ValueError:
        pass
    else:
        raise AssertionError('invalid code accepted')

class Client:
    def __init__(self):
        self._connected = True
        self._lock = threading.Lock()
        self._cmd_id = 0
        self._handle = lambda raw: None
        self._ws = self
        self.sent = []
    def send(self, raw):
        request = json.loads(raw)
        self.sent.append(request)
        self._handle(json.dumps({'type': 'CommandResult', 'commandId': request['id'], 'data': {'ResultString': 'ok'}}))

with tempfile.TemporaryDirectory() as folder:
    client = Client()
    original = client._handle
    bridge = runtime.Bridge(client, folder, folder, host='server.example', token=token)
    assert isinstance(bridge.transport, runtime.SessionTransport)
    assert bridge.transport.capture('edit ping')['data']['ResultString'] == 'ok'
    assert len(client.sent) == 1
    bridge.close()
    assert client._handle is original
    owner = runtime.Bridge(client, folder, folder, host='server.example', token='owner-secret')
    assert isinstance(owner.transport, runtime.HttpSessionTransport)
    http_calls = []
    owner.transport.capture = lambda command: http_calls.append(command) or {'ok': True}
    owner.capture('edit access eyJhY3Rpb24iOiJzZWxmIn0=')
    assert len(client.sent) == 2 and not http_calls
    owner.capture('edit spawnat 0 0 0 123')
    assert http_calls == ['edit spawnat 0 0 0 123'] and len(client.sent) == 2
    owner.close()
    assert client._handle is original
    shared = runtime.SessionTransport(client)
    owner = runtime.Bridge(client, folder, folder, host='server.example', token='owner-secret', access_transport=shared)
    owner.close()
    assert client._handle is shared.handler
    shared.close()
    assert client._handle is original
print('PASS: invitation validation, private-token-only codes, moderator WS dispatch once, cleanup, owner HTTP routing')
