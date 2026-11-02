const test = require('node:test');
const assert = require('node:assert/strict');
const {createServer, start, builders} = require('../server/server-kit/panel/server');
test('settings allowlist, numeric limits and safe broadcast encoding', () => {
  for (const name of ['DropAllOnDeath','IsPVPEnabled','HungerDealsDamage']) {
    assert.equal(builders.serverSetting({name,value:false}), `settings changesetting server ${name} false`);
    assert.throws(() => builders.serverSetting({name,value:'false; save full-wipe'}));
  }
  for (const name of ['RespawnTimeSeconds','TimeSpeedMultiplier','XPX','DamageX','PVPMultiplier','PVPCrippleMultiplier','HungerTick','CommunityStorageMultiplier']) {
    assert.equal(builders.serverSetting({name,value:1.5}), `settings changesetting server ${name} 1.5`);
    for (const value of [-1,Infinity,NaN,1e40,'1; quit']) assert.throws(() => builders.serverSetting({name,value}));
  }
  assert.throws(() => builders.serverSetting({name:'unknown',value:1}));
  const message = 'Hello "world";\nsettings reset';
  const command = builders.adminBroadcast({message});
  assert.equal(Buffer.from(command.split(' ')[2],'base64').toString('utf8'),message);
  assert.throws(() => builders.adminBroadcast({message:' '.repeat(20)}));
  assert.throws(() => builders.adminBroadcast({message:'x'.repeat(501)}));
  assert.throws(() => builders.adminSpawn({x:Infinity,y:1,z:2}));
});
test('admin HTTP routes forward commands and preserve server failures', async () => {
 const commands=[]; let reject=false;
 const server=createServer({signer:{},pipe:{send:async command=>{
   commands.push(command);
   return {ok:!reject,text:JSON.stringify({ok:!reject,note:'done',err:reject?'server refused':undefined})};
 }}});
 try {
  const port=await start(server,0);
  const post=async (route,body)=>(await fetch(`http://127.0.0.1:${port}/api/${route}`,{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify(body)})).json();
  assert.equal((await post('server-settings',{name:'XPX',value:2})).ok,true);
  assert.equal((await post('admin/broadcast',{message:'Hi all'})).data.note,'done');
  assert.equal((await post('admin/spawn',{x:-596.447,y:275.657,z:240.869})).ok,true);
  assert.equal(commands[2],'edit setserverspawn -596.447 275.657 240.869');
  assert.equal((await post('admin/spawn/reset',{})).ok,true);
  reject=true;
  assert.equal((await post('admin/spawn',{x:0,y:0,z:0})).ok,false);
  assert.equal((await post('server-settings',{name:'XPX',value:2})).ok,false);
  const count=commands.length;
  await post('server-settings',{name:'quit',value:1});
  assert.equal(commands.length,count);
 } finally { await new Promise(resolve=>server.close(resolve)); }
});
