'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
module.exports = async function checkWeddingTheme(page) {
  await page.evaluate(async () => {
    await document.fonts.ready;
    await Promise.all([...document.querySelectorAll('.brand img,.hero-floral,.cherub')].map(i => i.decode()));
  });
  const assets = await page.evaluate(() => ({
    images: [...document.querySelectorAll('.brand img,.hero-floral,.cherub')].map(i => ({src:i.getAttribute('src'),ok:i.complete && i.naturalWidth > 0})),
    fonts: [...document.fonts].map(f => ({name:f.family,status:f.status})),
    text: document.body.innerText,
    bodyFont: getComputedStyle(document.body).fontFamily,
    headingFont: getComputedStyle(document.querySelector('h1')).fontFamily,
    framePosition: getComputedStyle(document.body,'::before').position,
    green: getComputedStyle(document.documentElement).getPropertyValue('--green').trim()
  }));
  assert(assets.images.length === 5 && assets.images.every(i=>i.ok), 'Approved wedding artwork did not load');
  assert(assets.images.some(i=>i.src.endsWith('/cherub-phone.webp')));
  assert(assets.images.some(i=>i.src.endsWith('/cherub-modem.webp')));
  assert.equal(assets.images.filter(i=>i.src.endsWith('/approved-flowers.webp')).length, 2);
  for (const family of ['Cormorant Garamond','Libre Baskerville','Great Vibes'])
    assert(assets.fonts.some(f => f.name.replaceAll('"','') === family && f.status === 'loaded'), `Font not loaded: ${family}`);
  assert(assets.bodyFont.includes('Libre Baskerville') && assets.headingFont.includes('Cormorant Garamond'));
  assert.equal(assets.green, '#18372c');
  assert.equal(assets.framePosition, 'absolute', 'Page frame must not cross content at the viewport bottom');
  assert(assets.text.includes('within your household') && assets.text.includes('wider world'));
  assert(!assets.text.includes('questionable dance moves') && !assets.text.includes('Who’s behind the camera'));
  const layouts = [];
  const overlap = (a,b) => a.left < b.right-1 && a.right > b.left+1 && a.top < b.bottom-1 && a.bottom > b.top+1;
  for (const width of [320,390,768,1440]) {
    await page.setViewportSize({width,height:width===1440?1000:844});
    await page.evaluate(() => { window.scrollTo(0,0); return document.fonts.ready; });
    const layout = await page.evaluate(() => ({
      viewport:innerWidth,scroll:document.documentElement.scrollWidth,
      upload:document.querySelector('#drop-zone').getBoundingClientRect().toJSON(),
      input:document.querySelector('#guest-name').getBoundingClientRect().toJSON(),
      inputFont:parseFloat(getComputedStyle(document.querySelector('#guest-name')).fontSize),
      artwork:[...document.querySelectorAll('.hero-floral,.cherub')].map(i=>({
        src:i.getAttribute('src'),rect:i.getBoundingClientRect().toJSON(),
        pointer:getComputedStyle(i).pointerEvents,decorative:i.alt==='' && i.getAttribute('aria-hidden')==='true'
      })),
      text:[...document.querySelectorAll('#heading,.hero .eyebrow,.privacy-copy,.brand')].map(n=>n.getBoundingClientRect().toJSON())
    }));
    layouts.push(layout);
    assert(layout.scroll <= layout.viewport+1, `Horizontal overflow at ${width}px`);
    assert(layout.upload.top >= 0 && layout.upload.bottom < (width===1440?1000:844), `Upload chooser below first viewport: ${width}px`);
    assert(layout.upload.width >= 210 && layout.inputFont >= 16, 'Mobile chooser/input too small');
    for (const art of layout.artwork) {
      assert(art.rect.width>0 && art.rect.left>=14 && art.rect.right<=width-14 && art.rect.top>=14, `Clipped artwork at ${width}px: ${art.src}`);
      assert(art.decorative && art.pointer==='none', 'Decorative artwork must not capture input');
      for (const protectedRect of [...layout.text,layout.upload,layout.input])
        assert(!overlap(art.rect,protectedRect), `Artwork overlaps text or controls at ${width}px: ${art.src}`);
    }
    if (width===390 || width===1440) await page.screenshot({path:`artifacts/wedding-design-${width===390?'mobile':'desktop'}.png`,fullPage:true});
  }
  await page.setViewportSize({width:390,height:844});
  fs.writeFileSync('artifacts/wedding-design-checks.json',JSON.stringify({assets,layouts},null,2));
  console.log('PASS: approved phone/modem cherubs, contained flowers, local typography, 320/390/768/1440px layouts, no overlap and first-screen upload chooser.');
};
