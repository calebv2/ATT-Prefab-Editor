#!/usr/bin/env python3
# Local ATT server console client — runs ON the server box, talks to the loopback
# console (127.0.0.1:<console_port>). Mints a short-lived RS256 token with the
# private key in UserData/att_console/cli, so no tunnel needed.
#
#   sudo apt-get install -y python3-cryptography
#
#   python3 attcmd.py                     # interactive shell (att> prompt)
#   python3 attcmd.py player list         # one-shot command
#   python3 attcmd.py "edit players"      # quote anything with spaces
#
# In the shell: type commands; 'exit' / 'quit' / 'q' or Ctrl-D leaves the shell.
# To actually send the game's own 'quit' (shuts the server DOWN), prefix with
# '!'  ->  !quit   (the '!' escape force-sends any command verbatim).
#
import base64, json, os, sys, time, uuid, urllib.request, urllib.error
import xml.etree.ElementTree as ET
from cryptography.hazmat.primitives.asymmetric import rsa, padding
from cryptography.hazmat.primitives import hashes

DIR = os.environ.get("ATT_CONSOLE_DIR",
                     os.path.expanduser("~/att-server-docker/game/UserData/att_console"))


def _find_kid():
    # Key id auto-detection, same rules as the bot's tokenSigner.js: an explicit
    # ATT_CONSOLE_KID wins; otherwise the single *.priv.xml in cli/ is the key.
    # (PrefabEditorCore self-provisions per-server keys, so the id differs per box.)
    kid = os.environ.get("ATT_CONSOLE_KID")
    if kid:
        return kid
    cli = os.path.join(DIR, "cli")
    try:
        privs = [f[:-len(".priv.xml")] for f in os.listdir(cli) if f.endswith(".priv.xml")]
    except OSError as ex:
        sys.exit("console key dir not found: %s (%s) - has the server booted PrefabEditorCore once?" % (cli, ex))
    if not privs:
        sys.exit("no *.priv.xml in %s - has the server booted PrefabEditorCore once?" % cli)
    if len(privs) > 1:
        sys.exit("multiple keys in %s - set ATT_CONSOLE_KID" % cli)
    return privs[0]


KID = _find_kid()
PRIV = os.path.join(DIR, "cli", KID + ".priv.xml")


def console_port():
    try:
        with open(os.path.join(DIR, "console_port.txt")) as f:
            return int(f.read().strip())
    except Exception:
        return 1764


URL = "http://127.0.0.1:%d/console" % console_port()


def b64u(b):
    return base64.urlsafe_b64encode(b).rstrip(b"=")


def _field(root, tag):
    return int.from_bytes(base64.b64decode(root.find(tag).text), "big")


# Build the RSA private key from the .NET RSA XML (CRT parameters), once.
_r = ET.parse(PRIV).getroot()
_KEY = rsa.RSAPrivateNumbers(
    _field(_r, "P"), _field(_r, "Q"), _field(_r, "D"),
    _field(_r, "DP"), _field(_r, "DQ"), _field(_r, "InverseQ"),
    rsa.RSAPublicNumbers(_field(_r, "Exponent"), _field(_r, "Modulus")),
).private_key()

_tok = {"jwt": None, "exp": 0}


def token():
    now = int(time.time())
    if _tok["jwt"] and now < _tok["exp"] - 60:
        return _tok["jwt"]
    header = {"alg": "RS256", "typ": "JWT", "kid": KID}
    payload = {
        "UserId": "1",                 # must match a line in allowlist.txt
        "Username": "Owner",           # FromToken throws without this
        "is_verified": "true",
        "Policy": "game_access_public,server_access_pre_alpha,server_access_tutorial",
        "jti": uuid.uuid4().hex,
        "iat": now,
        "exp": now + 900,
    }
    signing = (b64u(json.dumps(header,  separators=(",", ":")).encode()) + b"." +
               b64u(json.dumps(payload, separators=(",", ":")).encode()))
    jwt = (signing + b"." +
           b64u(_KEY.sign(signing, padding.PKCS1v15(), hashes.SHA256()))).decode()
    _tok["jwt"], _tok["exp"] = jwt, now + 900
    return jwt


def _unwrap(s):
    # Peel the (BOM-prefixed, double-encoded) CommandResult to human text.
    s = s.lstrip("﻿")
    try:
        node = json.loads(s)
    except Exception:
        return s
    for _ in range(6):
        d = node.get("data", node) if isinstance(node, dict) else node
        if not isinstance(d, dict):
            break
        if d.get("Exception"):
            return "EXCEPTION: " + str(d["Exception"])
        rs = d.get("Result", d.get("ResultString"))
        if not isinstance(rs, str):
            return json.dumps(rs, indent=2) if rs is not None else "(no output)"
        inner = rs.lstrip("﻿").strip()
        if inner.startswith("{") and "CommandResult" in inner:
            node = json.loads(inner)
            continue
        return rs.replace("\r\n", "\n").rstrip()
    return s


def send(cmd):
    body = json.dumps({"content": cmd, "id": uuid.uuid4().hex}).encode()
    req = urllib.request.Request(URL, data=body, method="POST", headers={
        "Authorization": "Bearer " + token(),
        "Content-Type": "application/json",
    })
    try:
        raw = urllib.request.urlopen(req, timeout=20).read().decode("utf-8", "replace")
        return _unwrap(raw)
    except urllib.error.HTTPError as ex:
        detail = ex.read().decode("utf-8", "replace")
        if ex.code == 401:
            return "HTTP 401 (token rejected). Check att_console/audit.log for the reason."
        return "HTTP %d: %s" % (ex.code, detail)
    except urllib.error.URLError as ex:
        return "connect failed: %s  (is the console up on %s?)" % (ex.reason, URL)


def repl():
    print("ATT console @ %s  —  'exit' to leave, '!cmd' to force-send (e.g. !quit)" % URL)
    while True:
        try:
            line = input("att> ").strip()
        except (EOFError, KeyboardInterrupt):
            print()
            break
        if not line:
            continue
        if line.startswith("!"):
            print(send(line[1:].strip()))
        elif line.lower() in ("exit", "quit", "q"):
            break
        else:
            print(send(line))


if __name__ == "__main__":
    if len(sys.argv) > 1:
        print(send(" ".join(sys.argv[1:])))
    else:
        repl()
