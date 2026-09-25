// RS256 console-token signer — a copy of bot-dist/lib/tokenSigner.js (the
// native port of tooling/attcmd.py's token()), vendored here so the panel
// deploys standalone without reaching into the bot tree. Keep changes in sync.

'use strict';

const crypto = require('crypto');
const fs = require('fs');
const os = require('os');
const path = require('path');

function b64url(buf) {
    return buf.toString('base64').replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

// .NET RSAKeyValue XML -> Node private KeyObject via JWK.
function keyFromXml(xml) {
    const pick = (tag) => {
        const m = xml.match(new RegExp('<' + tag + '>([^<]*)</' + tag + '>'));
        if (!m) { throw new Error('private key XML missing <' + tag + '>'); }
        return b64url(Buffer.from(m[1], 'base64'));
    };
    const jwk = {
        kty: 'RSA',
        n: pick('Modulus'), e: pick('Exponent'), d: pick('D'),
        p: pick('P'), q: pick('Q'), dp: pick('DP'), dq: pick('DQ'), qi: pick('InverseQ')
    };
    return crypto.createPrivateKey({ key: jwk, format: 'jwk' });
}

function defaultConsoleDir() {
    if (process.env.ATT_CONSOLE_DIR) { return process.env.ATT_CONSOLE_DIR; }
    if (process.env.ATT_GAME_DIR) { return path.join(process.env.ATT_GAME_DIR, 'UserData', 'att_console'); }
    return path.join(os.homedir(), 'att-server-docker', 'game', 'UserData', 'att_console');
}

function locateKey(consoleDir) {
    const cliDir = path.join(consoleDir, 'cli');
    const wantKid = process.env.ATT_CONSOLE_KID;
    if (wantKid) {
        return { kid: wantKid, file: path.join(cliDir, wantKid + '.priv.xml') };
    }
    let priv;
    try {
        priv = fs.readdirSync(cliDir).filter((f) => f.endsWith('.priv.xml'));
    } catch (e) {
        throw new Error('console key dir not found: ' + cliDir + ' (' + e.message + ')');
    }
    if (priv.length === 0) { throw new Error('no *.priv.xml in ' + cliDir); }
    if (priv.length > 1) { throw new Error('multiple keys in ' + cliDir + '; set ATT_CONSOLE_KID'); }
    return { kid: priv[0].replace(/\.priv\.xml$/, ''), file: path.join(cliDir, priv[0]) };
}

class TokenSigner {
    constructor(opts = {}) {
        this._dir = opts.consoleDir || defaultConsoleDir();
        const { kid, file } = locateKey(this._dir);
        this._kid = kid;
        this._key = keyFromXml(fs.readFileSync(file, 'utf8'));
        this._userId = String(opts.userId || process.env.ATT_CONSOLE_USERID || '1');
        this._username = opts.username || process.env.ATT_CONSOLE_USERNAME || 'Owner';
        this._policy = opts.policy || process.env.ATT_CONSOLE_POLICY
            || 'game_access_public,server_access_pre_alpha,server_access_tutorial';
        this._ttl = opts.ttlSeconds || 900;
        this._cache = { jwt: null, exp: 0 };
    }

    get kid() { return this._kid; }
    get consoleDir() { return this._dir; }

    token() {
        const now = Math.floor(Date.now() / 1000);
        if (this._cache.jwt && now < this._cache.exp - 60) { return this._cache.jwt; }
        const header = { alg: 'RS256', typ: 'JWT', kid: this._kid };
        const payload = {
            UserId: this._userId,
            Username: this._username,
            is_verified: 'true',
            Policy: this._policy,
            jti: crypto.randomUUID().replace(/-/g, ''),
            iat: now,
            exp: now + this._ttl
        };
        const signing = b64url(Buffer.from(JSON.stringify(header)))
            + '.' + b64url(Buffer.from(JSON.stringify(payload)));
        const sig = crypto.sign('RSA-SHA256', Buffer.from(signing), {
            key: this._key, padding: crypto.constants.RSA_PKCS1_PADDING
        });
        const jwt = signing + '.' + b64url(sig);
        this._cache = { jwt, exp: payload.exp };
        return jwt;
    }
}

module.exports = { TokenSigner, keyFromXml, b64url, locateKey, defaultConsoleDir };
