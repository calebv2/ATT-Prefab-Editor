'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');
const {createServer, start, builders} = require('../server/server-kit/panel/server');

function request(port, route, body) {
    const wire = JSON.stringify(body);
    return new Promise((resolve, reject) => {
        const req = http.request({
            hostname: '127.0.0.1', port, path: '/api/edit/' + route, method: 'POST',
            headers: {'content-type': 'application/json', 'content-length': Buffer.byteLength(wire)}
        }, res => {
            let text = '';
            res.setEncoding('utf8');
            res.on('data', chunk => { text += chunk; });
            res.on('end', () => {
                try { resolve({status: res.statusCode, body: JSON.parse(text)}); }
                catch (error) { reject(error); }
            });
            res.on('error', reject);
        });
        req.setTimeout(3000, () => req.destroy(new Error('test HTTP request timed out')));
        req.on('error', reject);
        req.end(wire);
    });
}

async function withPanel(action) {
    const commands = [];
    const server = createServer({signer: {}, pipe: {send: async command => {
        commands.push(command);
        return {ok: true, text: JSON.stringify({ok: true, entity: {id: 1234}})};
    }}});
    try {
        const port = await start(server, 0);
        await action(port, commands);
    } finally {
        await new Promise(resolve => server.close(resolve));
    }
}

test('raw comma and pipe save strings remain one exact command token', () => {
    assert.equal(builders.editPaste({x: -12.125, y: 275.657, z: 240.869, string: '30016,1,0,0,0'}),
        'edit paste -12.125 275.657 240.869 30016,1,0,0,0');
    assert.equal(builders.editReplace({id: 87, string: '30016,1|0,0,0|1,1,1'}),
        'edit replace 87 30016,1|0,0,0|1,1,1');
    // Preserve the existing clipboard normalization rather than changing its grammar.
    assert.equal(builders.editPaste({x: 1, y: 2, z: 3, string: ' \r\n1, 2 ,3 | 4,5\t '}),
        'edit paste 1 2 3 1,2,3|4,5');
});

test('paste and replace routes forward strings and coordinates exactly once', async () => {
    await withPanel(async (port, commands) => {
        const paste = await request(port, 'paste', {x: -12.125, y: 275.657, z: 240.869, string: '30016,1,0,0,0'});
        assert.equal(paste.status, 200);
        assert.equal(paste.body.ok, true);
        assert.equal(paste.body.data.entity.id, 1234);
        assert.deepEqual(commands, ['edit paste -12.125 275.657 240.869 30016,1,0,0,0']);
        const replace = await request(port, 'replace', {id: 87, string: '30016,1|0,0,0|1,1,1'});
        assert.equal(replace.status, 200);
        assert.equal(replace.body.ok, true);
        assert.deepEqual(commands, [
            'edit paste -12.125 275.657 240.869 30016,1,0,0,0',
            'edit replace 87 30016,1|0,0,0|1,1,1'
        ]);
    });
});

test('both routes accept strings above 64 KiB through the existing 200000 character cap', async () => {
    await withPanel(async (port, commands) => {
        const aboveLegacyBodyLimit = '1,'.repeat(40000);
        const atStringLimit = '1,'.repeat(100000);
        assert.ok(aboveLegacyBodyLimit.length > 65536);
        assert.equal(atStringLimit.length, 200000);
        for (const string of [aboveLegacyBodyLimit, atStringLimit]) {
            let count = commands.length;
            const paste = await request(port, 'paste', {x: 1.25, y: -2.5, z: 3, string});
            assert.equal(paste.status, 200);
            assert.equal(paste.body.ok, true);
            assert.equal(commands.length, count + 1);
            assert.equal(commands[count], 'edit paste 1.25 -2.5 3 ' + string);
            count = commands.length;
            const replace = await request(port, 'replace', {id: 87, string});
            assert.equal(replace.status, 200);
            assert.equal(replace.body.ok, true);
            assert.equal(commands.length, count + 1);
            assert.equal(commands[count], 'edit replace 87 ' + string);
        }
    });
});

test('oversized strings, source code and quote injection never reach the command pipe', async () => {
    await withPanel(async (port, commands) => {
        const invalid = [
            '1,'.repeat(100000) + '1',
            'using System; class Item { public int Id = 30016; }',
            'const item = "30016,1,0"; console.log(item);',
            '30016,1,0"; save full-wipe',
            '30016,1,0"',
            '30016,1,0;1',
            '30016,1,0`'
        ];
        for (const string of invalid) {
            for (const route of ['paste', 'replace']) {
                const result = await request(port, route, {id: 87, x: 1, y: 2, z: 3, string});
                assert.equal(result.status, 400, route + ' rejects invalid string');
                assert.match(result.body.error, /valid save string/);
                assert.equal(commands.length, 0, route + ' must not dispatch invalid data');
            }
        }
    });
});

test('HTTP bodies above 256 KiB are rejected without dispatch, including connection-reset behavior', async () => {
    await withPanel(async (port, commands) => {
        const string = '1,'.repeat(140000);
        assert.ok(Buffer.byteLength(JSON.stringify({string})) > 256 * 1024);
        for (const route of ['paste', 'replace']) {
            try {
                const result = await request(port, route, {id: 87, x: 1, y: 2, z: 3, string});
                assert.equal(result.status, 400);
                assert.match(result.body.error, /body too large/);
            } catch (error) {
                // readBody deliberately destroys an oversized request, so HTTP may
                // reset before a JSON error can be delivered. Other failures fail.
                assert.equal(error.code, 'ECONNRESET');
            }
            await new Promise(resolve => setImmediate(resolve));
            assert.equal(commands.length, 0, route + ' must not dispatch an oversized body');
        }
    });
});
