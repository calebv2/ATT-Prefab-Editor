'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createServer, start, builders } = require('../server/server-kit/panel/server');

test('teleporter coordinates and nudge inputs are validated', () => {
    assert.equal(builders.editLinkTeleporter({id:42,x:-596.447,y:275.657,z:240.869}),
        'edit linkteleporter 42 -596.447 275.657 240.869');
    for (const invalid of [NaN, Infinity, '-1; quit']) {
        assert.throws(() => builders.editLinkTeleporter({id:42,x:invalid,y:0,z:0}));
    }
    for (const direction of [0,2,'1']) assert.throws(() => builders.editNudge({player:'Test',direction}));
    assert.match(builders.editNudge({player:'Test Player',direction:-1}), /edit nudge .*Test Player.* -1$/);
});

test('new routes deliver commands and preserve game rejection instead of reporting success', async () => {
    const stateDir = fs.mkdtempSync(path.join(os.tmpdir(), 'prefab-teleporter-test-'));
    const commands = [];
    let reject = false;
    const server = createServer({stateDir, signer:{}, pipe:{send: async command => {
        commands.push(command);
        return {ok:true,text:JSON.stringify(reject ? {ok:false,err:'no map chunk'} : {ok:true,entity:{id:42}})};
    }}});
    try {
        const port = await start(server, 0);
        const post = async (route, body) => (await fetch('http://127.0.0.1:' + port + '/api/edit/' + route,
            {method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify(body)})).json();
        assert.equal((await post('nudge',{player:'Test',direction:1})).ok,true);
        assert.equal((await post('link-teleporter',{id:42,x:-596.447,y:275.657,z:240.869})).data.entity.id,42);
        assert.equal(commands[1],'edit linkteleporter 42 -596.447 275.657 240.869');
        reject = true;
        const result = await post('link-teleporter',{id:42,x:0,y:1,z:2});
        assert.equal(result.ok,false);
        assert.equal(result.data.err,'no map chunk');
        const count = commands.length;
        assert.equal((await post('nudge',{player:'Test',direction:500})).ok,false);
        assert.equal(commands.length,count);
    } finally {
        await new Promise(resolve => server.close(resolve));
        fs.rmSync(stateDir,{recursive:true,force:true});
    }
});
