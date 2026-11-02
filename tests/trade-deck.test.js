'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createServer, start, builders } = require('../server/server-kit/panel/server');
const body = {id:42,itemHash:123,price:50,perPurchase:5,stock:100};
test('trade requests validate every quantity and reject injected commands', () => {
    assert.equal(builders.editTradeInfo({id:42}), 'edit tradeinfo 42');
    assert.equal(builders.editTradeSet(body), 'edit tradeset 42 123 50 5 100');
    for (const key of ['itemHash','price','perPurchase','stock'])
        for (const invalid of [0,-1,1.5,NaN,Infinity,'1; quit',null])
            assert.throws(() => builders.editTradeSet({...body,[key]:invalid}));
    assert.throws(() => builders.editTradeSet({...body,stock:4}));
    assert.throws(() => builders.editTradeSet({...body,id:'42; quit'}));
});
test('trade routes forward exact settings and preserve server validation failures', async () => {
    const stateDir = fs.mkdtempSync(path.join(os.tmpdir(),'trade-test-'));
    const commands=[]; let reject=false;
    const server=createServer({stateDir,signer:{},pipe:{send:async command=>{
        commands.push(command); return {ok:true,text:JSON.stringify(reject?{ok:false,err:'empty current stock first'}:{ok:true,entity:{id:42},trade:{price:50,perPurchase:5,stock:100}})};
    }}});
    try {
        const port=await start(server,0);
        const post=async(route,b)=>(await fetch('http://127.0.0.1:'+port+'/api/edit/'+route,{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify(b)})).json();
        assert.equal((await post('trade-info',{id:42})).data.trade.stock,100);
        assert.equal((await post('trade-set',body)).ok,true);
        assert.deepEqual(commands,['edit tradeinfo 42','edit tradeset 42 123 50 5 100']);
        assert.equal((await post('trade-add-stock',{id:42,itemHash:123,amount:9})).ok,true);
        assert.equal(commands[2],'edit tradeaddstock 42 123 9');
        reject=true;
        const result=await post('trade-set',body); assert.equal(result.ok,false);assert.equal(result.data.err,'empty current stock first');
        const count=commands.length;
        assert.equal((await post('trade-set',{...body,price:0})).ok,false); assert.equal(commands.length,count);
    } finally {await new Promise(resolve=>server.close(resolve));fs.rmSync(stateDir,{recursive:true,force:true});}
});

test('add stock validates positive amounts without imposing the old 10000 limit', () => {
    assert.equal(builders.editTradeAddStock({id:42,itemHash:123,amount:20000}), 'edit tradeaddstock 42 123 20000');
    for (const amount of [0,-1,1.5,2147483648,'2; quit',null])
        assert.throws(() => builders.editTradeAddStock({id:42,itemHash:123,amount}));
    assert.equal(builders.editTradeSet({...body,stock:20000}), 'edit tradeset 42 123 50 5 20000');
});
