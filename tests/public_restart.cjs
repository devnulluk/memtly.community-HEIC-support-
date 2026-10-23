const {chromium} = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs');
(async()=>{
 const state=JSON.parse(fs.readFileSync('artifacts/public-browser-state.json'));
 const browser=await chromium.launch();
 try {
  const a=await browser.newContext({ignoreHTTPSErrors:true,storageState:state.alice});
  const b=await browser.newContext({ignoreHTTPSErrors:true,storageState:state.bob});
  const admin=await browser.newContext({ignoreHTTPSErrors:true,storageState:state.admin});
  const base='https://wedding.test:5443';
  const session=await(await a.request.get(base+'/Wedding/Session')).json();
  assert.equal(session.invited,true); assert.equal(session.publicAccess,true); assert.equal(session.sharedGallery,false);
  const items=await(await a.request.get(base+'/Wedding/Items')).json();
  assert.equal(items.total,2); assert(items.items.some(i=>i.id===state.id));
  assert(!items.items.some(i=>i.id===state.bobId));
  const other=await(await b.request.get(base+'/Wedding/Items')).json();
  assert.deepEqual(other.items.map(i=>i.id),[state.bobId]);
  assert.equal((await b.request.get(base+'/Wedding/Preview/'+state.id)).status(),404);
  assert.equal((await b.request.get(base+'/Wedding/Video/'+state.videoId)).status(),404);
  assert.equal((await(await admin.request.get(base+'/Wedding/Session')).json()).admin,true);
  assert.equal((await(await admin.request.get(base+'/Wedding/AdminItems')).json()).total,3);
  const master=await admin.request.get(base+'/Wedding/Original/'+state.id);
  assert.deepEqual(await master.body(),fs.readFileSync('artifacts/browser-original.png'));
  console.log('PASS: both existing browser identities, owner-only photo/video access, administrator session, media and originals survive restart.');
 } finally {await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
