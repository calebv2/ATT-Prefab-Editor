// The console pipe: authenticated POST /console to PrefabEditorCore's loopback
// server (default 127.0.0.1:1764), plus the CommandResult unwrapping. The
// transport + unwrap logic mirror bot-dist/lib/localConsole.js — same
// double-encoded envelope, same structured-Result passthrough.

'use strict';

const fs = require('fs');
const http = require('http');
const path = require('path');
const { execFile } = require('child_process');

const DEFAULT_PORT = 1764;

// The mod writes the live console port to UserData/<dir>/console_port.txt.
function resolvePort(consoleDir, fallback) {
    if (consoleDir) {
        try {
            const raw = fs.readFileSync(path.join(consoleDir, 'console_port.txt'), 'utf8').trim();
            const n = parseInt(raw, 10);
            if (n > 0) { return n; }
        } catch { /* file absent -> use fallback */ }
    }
    return fallback || DEFAULT_PORT;
}

function postConsole(host, port, token, command) {
    return new Promise((resolve, reject) => {
        const body = JSON.stringify({ content: command, id: Math.floor(Math.random() * 0x100000000) });
        const req = http.request({
            host, port, path: '/console', method: 'POST',
            headers: {
                'Authorization': 'Bearer ' + token,
                'Content-Type': 'application/json',
                'Content-Length': Buffer.byteLength(body)
            },
            timeout: 30000
        }, (res) => {
            let data = '';
            res.on('data', (c) => { data += c; });
            res.on('end', () => resolve({ status: res.statusCode, body: data }));
        });
        req.on('timeout', () => req.destroy(new Error('console request timed out')));
        req.on('error', reject);
        req.write(body);
        req.end();
    });
}

// In Docker deployments the console intentionally listens on the game
// container's loopback. Use docker exec to reach it without publishing 1764.
function postConsoleViaDocker(container, port, token, command) {
    const body = JSON.stringify({ content: command, id: Math.floor(Math.random() * 0x100000000) });
    const args = [
        'exec', container, 'curl', '-sS', '--max-time', '30', '-X', 'POST',
        '-H', 'Authorization: Bearer ' + token,
        '-H', 'Content-Type: application/json',
        '--data-binary', body,
        '-w', '\n%{http_code}',
        'http://127.0.0.1:' + port + '/console'
    ];
    return new Promise((resolve, reject) => {
        execFile('docker', args, { timeout: 35000, maxBuffer: 2 * 1024 * 1024 }, (error, stdout, stderr) => {
            if (error) {
                reject(new Error('docker console transport failed: ' + (stderr || error.message).trim()));
                return;
            }
            const split = stdout.lastIndexOf('\n');
            if (split < 0) {
                reject(new Error('docker console transport returned no HTTP status'));
                return;
            }
            const status = parseInt(stdout.slice(split + 1).trim(), 10);
            if (!Number.isInteger(status)) {
                reject(new Error('docker console transport returned an invalid HTTP status'));
                return;
            }
            resolve({ status, body: stdout.slice(0, split) });
        });
    });
}

// Peel the double-encoded CommandResult down to clean text + any structured
// Result the command handler returned (see localConsole.js for the history).
function unwrapConsole(parsed) {
    let node = parsed;
    for (let depth = 0; depth < 6; depth++) {
        const d = node && typeof node === 'object' && node.data ? node.data : node;
        if (!d || typeof d !== 'object') { break; }
        if (d.Exception) { return { text: null, structured: null, exception: String(d.Exception) }; }
        const rs = d.Result != null ? d.Result : d.ResultString;
        if (typeof rs !== 'string') {
            return {
                text: typeof d.ResultString === 'string' ? d.ResultString
                    : (rs == null ? null : JSON.stringify(rs)),
                structured: rs == null ? null : rs,
                exception: null
            };
        }
        const inner = rs.replace(/^﻿/, '').trim();
        if (inner.startsWith('{') && inner.indexOf('CommandResult') !== -1) {
            try { node = JSON.parse(inner); continue; } catch { /* not nested after all */ }
        }
        return { text: rs, structured: null, exception: null };
    }
    return { text: null, structured: null, exception: null };
}

class ConsolePipe {
    // opts: { host, port, consoleDir, signer }
    constructor(opts) {
        this.host = opts.host || '127.0.0.1';
        this.port = resolvePort(opts.consoleDir, opts.port);
        this._signer = opts.signer;
        this.dockerContainer = process.env.ATT_CONSOLE_DOCKER_CONTAINER || '';
    }

    // -> { ok, text, structured, exception } ; throws on transport/auth failure
    async send(command) {
        const token = this._signer.token();
        const transport = this.dockerContainer
            ? postConsoleViaDocker(this.dockerContainer, this.port, token, command)
            : postConsole(this.host, this.port, token, command);
        const { status, body } = await transport;
        if (status === 401) {
            throw new Error('console rejected token (401): key not trusted or identity not allowlisted');
        }
        if (status !== 200) {
            throw new Error('console HTTP ' + status + ': ' + (body || '').slice(0, 200));
        }
        let parsed;
        try { parsed = JSON.parse(body); }
        catch { parsed = { data: { ResultString: body } }; }
        const { text, structured, exception } = unwrapConsole(parsed);
        return { ok: exception == null, text, structured, exception };
    }
}

module.exports = { ConsolePipe, unwrapConsole, resolvePort, postConsole };
