const {chromium} = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs');
(async()=>{
 const state=JSON.parse(fs.readFileSync('artifacts/public-browser-state.json'));
 const browser=await chromium.launch();
 try {
  const a=await browser.newContext({ignoreHTTPSErrors:true,storageState:state.alice});
  const admin=await browser.newContext({ignoreHTTPSErrors:true,storageState:state.admin});
  const base='https://wedding.test:5443';
  const session=await(await a.request.get(base+'/Wedding/Session')).json();
  assert.equal(session.invited,true);
  const items=await(await a.request.get(base+'/Wedding/Items')).json();
  assert(items.items.some(i=>i.id===state.id));
  assert.equal((await(await admin.request.get(base+'/Wedding/Session')).json()).admin,true);
  const master=await admin.request.get(base+'/Wedding/Original/'+state.id);
  assert.deepEqual(await master.body(),fs.readFileSync('artifacts/browser-original.png'));
  console.log('PASS: existing browser identity, administrator session, media and originals survive app restart.');
 } finally {await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
