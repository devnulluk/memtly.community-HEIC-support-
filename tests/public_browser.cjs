// Real Chromium contexts against the real Production application over Caddy HTTPS.
const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const crypto = require('node:crypto');
(async () => {
  const base = 'https://wedding.test:5443';
  const browser = await chromium.launch({ headless: true });
  const errors = [];
  const contexts = [];
  try {
    const context = async () => { const c = await browser.newContext({ ignoreHTTPSErrors: true, viewport: {width:390,height:844} }); contexts.push(c); return c; };
    const alice = await context(), bob = await context(), admin = await context();
    const a = await alice.newPage(), b = await bob.newPage(), m = await admin.newPage();
    for (const p of [a,b,m]) p.on('pageerror', e => errors.push(e.message));
    assert.equal((await a.goto(base + '/')).status(), 200);
    await a.locator('#guest:not([hidden])').waitFor();
    assert.equal(a.url(), base + '/');
    await require('./wedding_theme_checks.cjs')(a);
    const aCookies = await alice.cookies();
    const guestCookie = aCookies.find(c => c.name === '__Host-NewlandBrownGuest');
    assert(guestCookie && guestCookie.secure && guestCookie.httpOnly && guestCookie.path === '/');
    assert.equal(await a.locator('#files').isDisabled(), false);
    await a.locator('#guest-name').fill('Browser guest');
    await a.locator('#files').setInputFiles('artifacts/browser-original.png');
    await a.locator('#thank-you:not([hidden])').waitFor({timeout:60000});
    const session = await (await a.request.get(base + '/Wedding/Session')).json();
    const listing = await (await a.request.get(base + '/Wedding/Items')).json();
    assert.equal(listing.total,1);
    const id = listing.items[0].id;
    assert(!('sha256' in listing.items[0]) && !('bytes' in listing.items[0]));
    assert(!JSON.stringify(listing).includes('browser-original.png'));
    await b.goto(base + '/'); await b.locator('#guest:not([hidden])').waitFor();
    await b.locator('#uploads-tab').click();
    await b.getByText('Browser guest', {exact:true}).waitFor();
    assert.equal((await b.request.get(base + '/Wedding/Original/' + id, {maxRedirects:0})).status(),302);
    assert.equal((await b.request.get(base + '/Wedding/Export', {maxRedirects:0})).status(),302);
    await a.screenshot({path:'artifacts/wedding-root-mobile.png', fullPage:true});
    // Exercise the real Memtly login form and embedded webpack manifest.
    const login = await m.goto(base + '/Account/Login'); assert.equal(login.status(),200);
    const token = await m.locator('input[name="__RequestVerificationToken"]').first().inputValue();
    const loginResult = await m.request.post(base + '/Account/Login', { form: {
      Username:'admin', Password:process.env.WEDDING_TEST_PASSWORD, __RequestVerificationToken:token
    }});
    assert.equal(loginResult.status(),200);
    const adminSession = await (await m.request.get(base + '/Wedding/Session')).json();
    assert.equal(adminSession.admin,true);
    await m.goto(base + '/Wedding/Admin');
    await m.getByRole('heading', {name:'Our wedding collection',exact:true}).waitFor();
    const original = await m.request.get(base + '/Wedding/Original/' + id);
    assert.equal(original.status(),200);
    assert.deepEqual(await original.body(),fs.readFileSync('artifacts/browser-original.png'));
    const controls = {headers:{'X-Wedding-CSRF':adminSession.csrf}};
    assert.equal((await m.request.post(base + '/Wedding/Visibility/' + id, {...controls, data:{hidden:true}})).status(),200);
    assert.equal((await (await b.request.get(base + '/Wedding/Items')).json()).total,0);
    assert.equal((await b.request.get(base + '/Wedding/Preview/' + id)).status(),404);
    await m.request.post(base + '/Wedding/Visibility/' + id, {...controls, data:{hidden:false}});
    m.on('dialog', d => d.accept());
    await m.locator('#toggle-uploads').click();
    await m.getByRole('button',{name:'Reopen uploads',exact:true}).waitFor();
    await a.reload(); await a.locator('#guest:not([hidden])').waitFor();
    assert.equal(await a.locator('#files').isDisabled(),true);
    const closed = await (await a.request.get(base + '/Wedding/Session')).json();
    const uploadHeaders = {'X-Wedding-CSRF':closed.csrf,'Content-Type':'application/octet-stream','X-File-Name':'closed.png','X-Upload-ID':crypto.randomUUID().replaceAll('-','')};
    assert.equal((await a.request.post(base + '/Wedding/Upload',{headers:uploadHeaders,data:fs.readFileSync('artifacts/browser-original.png')})).status(),403);
    await m.locator('#toggle-uploads').click();
    await m.getByRole('button',{name:'Close uploads',exact:true}).waitFor();
    await a.reload(); await a.locator('#guest:not([hidden])').waitFor();
    await a.locator('#files').setInputFiles('artifacts/browser-original.mp4');
    await a.locator('#thank-you:not([hidden])').waitFor({timeout:60000});
    let video;
    for(let i=0;i<90;i++) {
      const items = await (await b.request.get(base + '/Wedding/Items')).json();
      video = items.items.find(x=>x.video);
      if(video?.preview==='ready') break;
      await new Promise(r=>setTimeout(r,1000));
    }
    assert.equal(video?.preview,'ready');
    const preview = await b.request.get(base + '/Wedding/Video/' + video.id);
    assert.equal(preview.status(),200);
    assert.equal(preview.headers()['content-type'],'video/mp4');
    assert.notDeepEqual(await preview.body(),fs.readFileSync('artifacts/browser-original.mp4'));
    const fullVideo = await m.request.get(base + '/Wedding/Original/' + video.id);
    assert.deepEqual(await fullVideo.body(),fs.readFileSync('artifacts/browser-original.mp4'));
    assert.equal((await b.request.get(base + '/Wedding/Original/' + video.id,{maxRedirects:0})).status(),302);
    await b.reload(); await b.locator('#guest:not([hidden])').waitFor(); await b.locator('#uploads-tab').click();
    await b.getByRole('button',{name:'View Wedding video'}).waitFor();
    await b.getByRole('button',{name:'View Wedding video'}).click();
    await b.locator('dialog[open] video').waitFor();
    await m.reload(); await m.locator('#admin-tools:not([hidden])').waitFor();
    await m.evaluate(() => document.fonts.ready);
    await m.screenshot({path:'artifacts/wedding-admin-mobile.png', fullPage:true});
    assert.deepEqual(errors,[]);
    // Retain cookies and IDs for a restart-persistence check in the next invocation.
    fs.writeFileSync('artifacts/public-browser-state.json', JSON.stringify({alice:await alice.storageState(),admin:await admin.storageState(),id,videoId:video.id}));
    console.log('PASS: real Chromium, HTTPS secure identities, clean root, no-code upload, shared gallery, real admin login, private masters, hide/close controls, PNG and video previews, unchanged originals and zero page errors.');
  } finally { for(const c of contexts) await c.close(); await browser.close(); }
})().catch(error => { console.error(error); process.exitCode=1; });
