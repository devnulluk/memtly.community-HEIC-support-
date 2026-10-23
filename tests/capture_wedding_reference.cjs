// Read-only visual reference capture of the couple's public wedding homepage.
// No forms, RSVP endpoints, admin pages, credentials or authenticated sessions.
const { chromium } = require('playwright');
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
(async () => {
  const out = 'design-reference';
  fs.mkdirSync(out + '/assets', {recursive:true});
  const browser = await chromium.launch({headless:true});
  const context = await browser.newContext({viewport:{width:1440,height:1000}});
  const page = await context.newPage();
  const pending = [], assets = [], errors = [];
  page.on('pageerror', e => errors.push(e.message));
  page.on('response', response => {
    const u = new URL(response.url());
    const type = response.request().resourceType();
    if (!['stylesheet','image','font','script'].includes(type) || !['wedding.newland-brown.com','fonts.googleapis.com','fonts.gstatic.com'].includes(u.hostname)) return;
    pending.push((async () => {
      try {
        const data = await response.body();
        if (!response.ok() || data.length > 8*1024*1024 || assets.length >= 80) return;
        const name = crypto.createHash('sha256').update(response.url()).digest('hex').slice(0,12) + '-' + (path.basename(u.pathname).replace(/[^a-zA-Z0-9._-]/g,'_') || 'asset');
        fs.writeFileSync(out+'/assets/'+name,data);
        assets.push({url:response.url(),file:'assets/'+name,type,bytes:data.length,contentType:response.headers()['content-type']});
      } catch(e) { errors.push('Asset: '+e.message); }
    })());
  });
  const response = await page.goto('https://wedding.newland-brown.com/',{waitUntil:'networkidle',timeout:60000});
  if (!response || !response.ok()) throw new Error('Reference homepage unavailable: '+response?.status());
  await page.evaluate(() => document.fonts.ready);
  await page.screenshot({path:out+'/reference-desktop.png',fullPage:true});
  fs.writeFileSync(out+'/index.html',await page.content());
  const design = await page.evaluate(() => ({
    title:document.title,
    text:document.body.innerText.slice(0,16000),
    images:[...document.images].map(i=>({src:i.currentSrc||i.src,alt:i.alt,width:i.naturalWidth,height:i.naturalHeight})),
    styles:[...document.styleSheets].map(s=>{try{return {href:s.href,css:[...s.cssRules].map(r=>r.cssText).join('\n')}}catch{return {href:s.href}}}),
    elements:[...document.querySelectorAll('body,main,header,h1,h2,h3,p,a,button,input,label')].slice(0,90).map(el=>{const s=getComputedStyle(el); return {tag:el.tagName,id:el.id,classes:el.className,text:el.textContent.trim().slice(0,100),font:s.fontFamily,size:s.fontSize,weight:s.fontWeight,color:s.color,background:s.backgroundColor,border:s.border,radius:s.borderRadius,letterSpacing:s.letterSpacing}})
  }));
  fs.writeFileSync(out+'/design.json',JSON.stringify(design,null,2));
  await page.setViewportSize({width:390,height:844});
  await page.screenshot({path:out+'/reference-mobile.png',fullPage:true});
  await Promise.all(pending);
  fs.writeFileSync(out+'/assets.json',JSON.stringify(assets,null,2));
  fs.writeFileSync(out+'/errors.json',JSON.stringify(errors,null,2));
  console.log('Captured public design:',design.title,'assets:',assets.length);
  await browser.close();
})().catch(e=>{console.error(e);process.exit(1);});
